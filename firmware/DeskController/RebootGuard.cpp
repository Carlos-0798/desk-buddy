#include "RebootGuard.h"
#include <Arduino.h>
#include <InternalFileSystem.h>
#include <flash/flash_nrf5x.h>

namespace deskreboot {
namespace {
volatile State currentState = State::Idle;
volatile Failure currentFailure = Failure::None;
volatile uint32_t generation = 0;
volatile uint32_t requestedAt = 0;
volatile bool noticeReleased = false;

bool isWaiting(State value) {
  return value == State::Queued || value == State::WaitingForFs || value == State::Flushing;
}
// Called only inside a short critical section. Never surround flash/FS waits
// with scheduler suspension or IRQ masking: their completion needs the SOC task.
bool currentLocked(uint32_t token, uint32_t now) {
  if (token != generation || !isWaiting(currentState)) return false;
  if (uint32_t(now - requestedAt) >= WaitTimeoutMs) {
    currentFailure = Failure::Timeout; currentState = State::Failed; ++generation;
    return false;
  }
  return true;
}

void queuedReset(uint32_t token) {
  // request() may yield while enqueueing. Let the caller attempt its bounded
  // serial notice first; an absent host is never a condition for reboot.
  for (;;) {
    const uint32_t now = millis();
    taskENTER_CRITICAL();
    const bool active = currentLocked(token, now);
    const bool ready = active && noticeReleased;
    if (ready) currentState = State::WaitingForFs;
    taskEXIT_CRITICAL();
    if (!active) return;
    if (ready) break;
    delay(1);
  }

  // AdaCallback is FIFO: earlier bond/CCCD saves have returned before here.
  // The filesystem mutex also excludes writes already running in other tasks.
  // Keep that mutex until reset; releasing it first would reopen the erase race.
  InternalFS._lockFS();
  taskENTER_CRITICAL();
  const bool afterLock = currentLocked(token, millis());
  if (afterLock) currentState = State::Flushing;
  taskEXIT_CRITICAL();
  if (!afterLock) { InternalFS._unlockFS(); return; }

  flash_nrf5x_flush(); // BSP waits for SoftDevice completion, but exposes no error result.

  taskENTER_CRITICAL();
  const bool afterFlush = currentLocked(token, millis());
  if (afterFlush) {
    currentState = State::Resetting;
    NVIC_SystemReset(); // Flash has returned; no blocking flash operation is inside this critical section.
  }
  taskEXIT_CRITICAL();
  // Reached on timeout; also reachable when a host-test reset stub returns.
  InternalFS._unlockFS();
}
}

bool request(uint32_t now) {
  taskENTER_CRITICAL();
  if (currentState != State::Idle) { taskEXIT_CRITICAL(); return false; }
  requestedAt = now; currentFailure = Failure::None; noticeReleased = false;
  const uint32_t token = ++generation; currentState = State::Queued;
  taskEXIT_CRITICAL();
  // This public BSP API can report allocation failure. Internal queue delivery
  // failures are not surfaced by BSP 1.7.0, so poll() supplies a no-reset timeout.
  if (!ada_callback(nullptr, 0, queuedReset, token)) {
    taskENTER_CRITICAL();
    if (generation == token && isWaiting(currentState)) {
      currentFailure = Failure::Enqueue; currentState = State::Failed; ++generation;
    }
    taskEXIT_CRITICAL();
    return false;
  }
  return true;
}
void releaseAfterNotice() {
  taskENTER_CRITICAL();
  if (currentState == State::Queued) noticeReleased = true;
  taskEXIT_CRITICAL();
}
void poll(uint32_t now) {
  taskENTER_CRITICAL();
  if (isWaiting(currentState)) currentLocked(generation, now);
  taskEXIT_CRITICAL();
}
bool pending() { return state() != State::Idle; }
State state() { taskENTER_CRITICAL(); const State value = currentState; taskEXIT_CRITICAL(); return value; }
Failure failure() { taskENTER_CRITICAL(); const Failure value = currentFailure; taskEXIT_CRITICAL(); return value; }
const char* failureName() {
  switch (failure()) {
    case Failure::Enqueue: return "enqueue";
    case Failure::Timeout: return "timeout";
    default: return "none";
  }
}
}
