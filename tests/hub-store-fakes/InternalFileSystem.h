#pragma once
#include "Adafruit_LittleFS.h"
class InternalFileSystem : public Adafruit_LittleFS {
 public:
  bool begin() { ++derivedBeginCalls; return mountOkay; }
};
extern InternalFileSystem InternalFS;
