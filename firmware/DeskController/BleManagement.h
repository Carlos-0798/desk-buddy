#pragma once
#include <Arduino.h>
#include <bluefruit.h>
#include "ManagementCore.h"
namespace deskble {
bool begin(); // Called once after Bluefruit.begin + HID service registration.
void advertise();
void event(ble_evt_t* event); // BLE event-task hook; no parsing/storage here.
void authorize(bool enable,uint32_t now); // Only physical UI / wired USB calls.
void tick(uint32_t now,bool hidBusy); // Loop only, one nonblocking fragment.
bool takeLine(char*& line,bool& invalid);
bool allowed();
void touch(uint32_t now); // Refresh 15s authorized-session idle deadline.
bool radioWanted();
uint8_t state();
uint32_t remaining(uint32_t now);
bool linked();bool secured();bool bonded();
uint32_t generation();
bool ready();
uint16_t rxHandle();uint16_t txHandle();uint16_t txCccdHandle();
uint16_t serviceChangedStart();uint16_t serviceChangedEnd();
uint32_t serviceChangedResult();
uint8_t serviceChangedAttempts();
bool serviceChangedConfirmed();
Print& response();
void startResponse();void finishResponse();
}
