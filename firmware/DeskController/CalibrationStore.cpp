#include "CalibrationStore.h"

#include <Adafruit_LittleFS.h>
#include <InternalFileSystem.h>
#include <string.h>

namespace deskstore {
namespace {

using Adafruit_LittleFS_Namespace::File;
using Adafruit_LittleFS_Namespace::FILE_O_READ;
using Adafruit_LittleFS_Namespace::FILE_O_WRITE;

const char* const kSlotPaths[2] = {"/deskcal0.bin", "/deskcal1.bin"};
const uint16_t kVersion = 1;
const uint16_t kRecordSize = 36;
const uint16_t kCrcOffset = 32;
const int32_t kEndpointMinimum = -1024;
const int32_t kEndpointMaximum = 5119;

bool allBytesErased(const volatile uint8_t* data, uint32_t length) {
  for (uint32_t i = 0; i < length; ++i) {
    if (data[i] != 0xFFu) return false;
  }
  return true;
}

// RAM representation only. The disk record is explicitly serialized, never
// memcpy'd from this struct: magic[4], version:u16, length:u16, sequence:u32,
// four endpoints:u32, swap:u8, sign:u8, reserved[2], CRC32:u32 (little endian).
struct Record {
  deskcore::TouchCalibration calibration;
  int8_t encoderSign;
  uint32_t sequence;
};

bool mountWithoutFormatting() {
  // InternalFileSystem::begin() in BSP 1.7.0 erases/formats on mount failure.
  // Explicitly call its base implementation: a bad/unformatted filesystem
  // must produce failure, never destroy BLE bonds or any other stored files.
  return InternalFS.Adafruit_LittleFS::begin();
}

uint16_t read16(const uint8_t* p) {
  return static_cast<uint16_t>(p[0]) |
         (static_cast<uint16_t>(p[1]) << 8);
}

uint32_t read32(const uint8_t* p) {
  return static_cast<uint32_t>(p[0]) |
         (static_cast<uint32_t>(p[1]) << 8) |
         (static_cast<uint32_t>(p[2]) << 16) |
         (static_cast<uint32_t>(p[3]) << 24);
}

int32_t readSigned32(const uint8_t* p) {
  const uint32_t bits = read32(p);
  // Decode two's-complement without implementation-defined uint32->int32
  // conversion for values above INT32_MAX, including the INT32_MIN case.
  return bits <= 0x7FFFFFFFu ? static_cast<int32_t>(bits)
                            : -1 - static_cast<int32_t>(~bits);
}

void write16(uint8_t* p, uint16_t value) {
  p[0] = static_cast<uint8_t>(value);
  p[1] = static_cast<uint8_t>(value >> 8);
}

void write32(uint8_t* p, uint32_t value) {
  for (uint8_t i = 0; i < 4; ++i) {
    p[i] = static_cast<uint8_t>(value >> (8u * i));
  }
}

uint32_t crc32(const uint8_t* bytes, uint16_t length) {
  uint32_t crc = 0xFFFFFFFFu;
  for (uint16_t i = 0; i < length; ++i) {
    crc ^= bytes[i];
    for (uint8_t bit = 0; bit < 8; ++bit) {
      crc = (crc >> 1) ^ ((crc & 1u) ? 0xEDB88320u : 0u);
    }
  }
  return crc ^ 0xFFFFFFFFu;
}

bool validCalibration(const deskcore::TouchCalibration& c, int8_t sign) {
  if (sign != 1 && sign != -1) return false;
  if (c.xLeft < kEndpointMinimum || c.xLeft > kEndpointMaximum ||
      c.xRight < kEndpointMinimum || c.xRight > kEndpointMaximum ||
      c.yTop < kEndpointMinimum || c.yTop > kEndpointMaximum ||
      c.yBottom < kEndpointMinimum || c.yBottom > kEndpointMaximum) {
    return false;
  }
  const int32_t xSpan = c.xRight - c.xLeft;
  const int32_t ySpan = c.yBottom - c.yTop;
  return (xSpan >= 500 || xSpan <= -500) &&
         (ySpan >= 500 || ySpan <= -500);
}

bool sameCalibration(const Record& record,
                     const deskcore::TouchCalibration& c, int8_t sign) {
  const deskcore::TouchCalibration& old = record.calibration;
  return old.xLeft == c.xLeft && old.xRight == c.xRight &&
         old.yTop == c.yTop && old.yBottom == c.yBottom &&
         old.swapXY == c.swapXY && record.encoderSign == sign;
}

void encode(const Record& record, uint8_t* bytes) {
  memset(bytes, 0, kRecordSize);
  memcpy(bytes, "DCLB", 4);
  write16(bytes + 4, kVersion);
  write16(bytes + 6, kRecordSize);
  write32(bytes + 8, record.sequence);
  write32(bytes + 12, static_cast<uint32_t>(record.calibration.xLeft));
  write32(bytes + 16, static_cast<uint32_t>(record.calibration.xRight));
  write32(bytes + 20, static_cast<uint32_t>(record.calibration.yTop));
  write32(bytes + 24, static_cast<uint32_t>(record.calibration.yBottom));
  bytes[28] = record.calibration.swapXY ? 1 : 0;
  bytes[29] = record.encoderSign == 1 ? 1 : 0xFF;
  write32(bytes + kCrcOffset, crc32(bytes, kCrcOffset));
}

bool decode(const uint8_t* bytes, Record& output) {
  if (memcmp(bytes, "DCLB", 4) != 0 || read16(bytes + 4) != kVersion ||
      read16(bytes + 6) != kRecordSize || bytes[28] > 1 ||
      (bytes[29] != 1 && bytes[29] != 0xFF) ||
      bytes[30] != 0 || bytes[31] != 0 ||
      read32(bytes + kCrcOffset) != crc32(bytes, kCrcOffset)) {
    return false;
  }
  Record candidate = {};
  candidate.sequence = read32(bytes + 8);
  candidate.calibration.xLeft = readSigned32(bytes + 12);
  candidate.calibration.xRight = readSigned32(bytes + 16);
  candidate.calibration.yTop = readSigned32(bytes + 20);
  candidate.calibration.yBottom = readSigned32(bytes + 24);
  candidate.calibration.swapXY = bytes[28] == 1;
  candidate.encoderSign = bytes[29] == 1 ? 1 : -1;
  if (!validCalibration(candidate.calibration, candidate.encoderSign)) {
    return false;
  }
  output = candidate;
  return true;
}

bool readRecord(const char* path, Record& output) {
  File file(InternalFS);
  if (!file.open(path, FILE_O_READ)) return false;
  uint8_t bytes[kRecordSize];
  const bool complete = !file.isDirectory() && file.size() == kRecordSize &&
                        file.read(bytes, kRecordSize) == kRecordSize;
  file.close();
  return complete && decode(bytes, output);
}

int newestSlot(const Record* records, const bool* valid) {
  if (!valid[0]) return valid[1] ? 1 : -1;
  if (!valid[1]) return 0;
  // Sequence comparison modulo 2^32. Normal adjacent saves differ by one;
  // equal/ambiguous half-range sequences deterministically prefer slot zero.
  const uint32_t delta = records[1].sequence - records[0].sequence;
  return delta != 0 && delta < 0x80000000u ? 1 : 0;
}

bool removeIfPresent(const char* path) {
  return !InternalFS.exists(path) || InternalFS.remove(path);
}

}  // namespace

bool prepare() {
  if (mountWithoutFormatting()) return true;
#if defined(NRF52840_XXAA)
  // Verified against Adafruit nRF52 BSP 1.7.0 InternalFileSystem.cpp:
  // 7 * 4096 bytes, [0xED000, 0xF4000). Do not generalize to another target.
  const uintptr_t start = 0xED000u;
  const uint32_t length = 7u * 4096u;
  if (!allBytesErased(reinterpret_cast<const volatile uint8_t*>(start), length)) {
    return false;
  }
  // The user-authorized first-time setup may initialize only a fully erased
  // partition. Never use this recovery path for nonempty damaged storage.
  return InternalFS.begin();
#else
  // Unknown targets cannot safely infer an InternalFS partition location.
  return false;
#endif
}

bool load(deskcore::TouchCalibration& calibration, int8_t& encoderSign) {
  if (!mountWithoutFormatting()) return false;
  Record records[2] = {};
  const bool valid[2] = {readRecord(kSlotPaths[0], records[0]),
                         readRecord(kSlotPaths[1], records[1])};
  const int latest = newestSlot(records, valid);
  if (latest < 0) return false;
  calibration = records[latest].calibration;
  encoderSign = records[latest].encoderSign;
  return true;
}

bool save(const deskcore::TouchCalibration& calibration, int8_t encoderSign) {
  if (!validCalibration(calibration, encoderSign) || !mountWithoutFormatting()) {
    return false;
  }
  Record records[2] = {};
  const bool valid[2] = {readRecord(kSlotPaths[0], records[0]),
                         readRecord(kSlotPaths[1], records[1])};
  const int latest = newestSlot(records, valid);
  if (latest >= 0 && sameCalibration(records[latest], calibration, encoderSign)) {
    return true;
  }

  const int target = latest == 0 ? 1 : 0;
  Record candidate = {};
  candidate.calibration = calibration;
  candidate.encoderSign = encoderSign;
  candidate.sequence = latest < 0 ? 1u : records[latest].sequence + 1u;
  uint8_t bytes[kRecordSize];
  encode(candidate, bytes);

  // FILE_O_WRITE appends in this BSP: replace only the non-latest slot.
  // Keep the newest valid record untouched through every checked failure.
  // BSP 1.7.0's littlefs rename path can leave an unpublished destination
  // after a directory split, so do not use a temporary-file rename at all.
  // The legacy /deskcal.tmp is deliberately ignored and left unchanged.
  if (!removeIfPresent(kSlotPaths[target])) return false;
  File file(InternalFS);
  if (!file.open(kSlotPaths[target], FILE_O_WRITE)) return false;
  const bool complete = file.write(bytes, kRecordSize) == kRecordSize;
  file.flush();
  file.close();
  Record verified = {};
  if (!complete || !readRecord(kSlotPaths[target], verified) ||
      verified.sequence != candidate.sequence ||
      !sameCalibration(verified, calibration, encoderSign)) {
    return false;
  }

  return true;
}

}  // namespace deskstore
