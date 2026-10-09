#pragma once
#include <stdint.h>

// Only coordinates a requested software reset. This does not repair a damaged
// filesystem or make the BSP's 4 KB read/erase/program cache power-fail atomic.
namespace deskreboot {
enum class State : uint8_t { Idle, Queued, WaitingForFs, Flushing, Resetting, Failed };
enum class Failure : uint8_t { None, Enqueue, Timeout };
constexpr uint32_t WaitTimeoutMs = 5000;

// Single request per boot. Failure stays latched, with output disabled, so a
// late callback can never turn a failed request into an unexpected reset.
bool request(uint32_t now);
void releaseAfterNotice();
void poll(uint32_t now);
bool pending();
State state();
Failure failure();
const char* failureName();
}
