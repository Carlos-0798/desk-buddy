#pragma once
#include <Arduino.h>
// One loop-owned CDC writer. No FIFO-full retry, raw Serial.write, or flush.
// This bounds application work, not the BSP's internal short mutex operations.
namespace deskusb {
Print& out();
void poll(uint32_t now);
void pump(uint32_t now);
bool ready();
bool canReply();
bool beginReply();
void finishReply();
int read();
uint32_t generation();
uint32_t droppedLines();
uint32_t txTimeouts();
uint32_t rxOverflows();
uint16_t queuedBytes();
bool stalled();
}
