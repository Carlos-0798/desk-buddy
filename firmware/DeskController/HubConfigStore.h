#pragma once
#include "HubConfig.h"
namespace deskconfigstore {
bool load(deskconfig::Bank& bank);
bool save(const deskconfig::Bank& bank);
// Diagnostics only; files, calibration and bonds are never exposed to the Hub.
const char* lastErrorName();
}
