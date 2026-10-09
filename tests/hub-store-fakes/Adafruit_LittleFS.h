#pragma once
#include <stdint.h>
#include <stddef.h>
#include <string.h>

// In-memory API double for the methods used by the actual storage module.
// FILE_O_WRITE deliberately appends, matching installed BSP 1.7.0.
class Adafruit_LittleFS {
 public:
  struct Entry {
    char path[32];
    uint8_t bytes[512];
    uint32_t length;
    bool used;
    bool directory;
  };
  Entry files[8] = {};
  bool mountOkay = true;
  bool renameOkay = true;
  bool removeOkay = true;
  bool corruptWrite = false;
  size_t writeLimit = 512;
  unsigned baseBeginCalls = 0, derivedBeginCalls = 0, writes = 0, removes = 0;
  unsigned renames = 0;
  bool begin() { ++baseBeginCalls; return mountOkay; }
  int find(const char* path) {
    for (int i = 0; i < 8; ++i) {
      if (files[i].used && strcmp(files[i].path, path) == 0) return i;
    }
    return -1;
  }
  bool exists(const char* path) { return find(path) >= 0; }
  bool remove(const char* path) {
    ++removes;
    const int index = find(path);
    if (!removeOkay || index < 0) return false;
    files[index].used = false;
    return true;
  }
  bool rename(const char* from, const char* to) {
    ++renames;
    const int index = find(from);
    if (!renameOkay || index < 0 || exists(to)) return false;
    strcpy(files[index].path, to);
    return true;
  }
  int create(const char* path) {
    const int existing = find(path);
    if (existing >= 0) return existing;
    for (int i = 0; i < 8; ++i) {
      if (!files[i].used) {
        files[i] = Entry{};
        files[i].used = true;
        strcpy(files[i].path, path);
        return i;
      }
    }
    return -1;
  }
};

namespace Adafruit_LittleFS_Namespace {
enum { FILE_O_READ = 0, FILE_O_WRITE = 1 };
class File {
 public:
  explicit File(Adafruit_LittleFS& fs) : fs_(fs) {}
  bool open(const char* path, uint8_t mode) {
    index_ = mode == FILE_O_WRITE ? fs_.create(path) : fs_.find(path);
    cursor_ = index_ >= 0 && mode == FILE_O_WRITE ? fs_.files[index_].length : 0;
    return index_ >= 0;
  }
  bool isDirectory() { return fs_.files[index_].directory; }
  uint32_t size() { return fs_.files[index_].length; }
  int read(void* buffer, uint16_t count) {
    auto& e = fs_.files[index_];
    if (count > e.length - cursor_) count = static_cast<uint16_t>(e.length - cursor_);
    memcpy(buffer, e.bytes + cursor_, count);
    cursor_ += count;
    return count;
  }
  size_t write(const uint8_t* buffer, size_t count) {
    ++fs_.writes;
    auto& e = fs_.files[index_];
    if (count > fs_.writeLimit) count = fs_.writeLimit;
    if (count > sizeof(e.bytes) - cursor_) count = sizeof(e.bytes) - cursor_;
    memcpy(e.bytes + cursor_, buffer, count);
    cursor_ += static_cast<uint32_t>(count);
    e.length = cursor_;
    if (fs_.corruptWrite && count) e.bytes[0] ^= 1;
    return count;
  }
  void flush() {}
  void close() { index_ = -1; }
 private:
  Adafruit_LittleFS& fs_;
  int index_ = -1;
  uint32_t cursor_ = 0;
};
}
