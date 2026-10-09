#pragma once
#include "DisplaySleep.h"
namespace deskdisplaystore {
// Independent records: never share the configuration bank or language slots.
bool load(deskdisplay::Settings& settings);
bool save(const deskdisplay::Settings& settings);
const char* lastErrorName();
}
