#include "HubConfigStore.h"

#include <Adafruit_LittleFS.h>
#include <InternalFileSystem.h>
#include <string.h>

namespace deskconfigstore {
namespace {

using Adafruit_LittleFS_Namespace::File;
using Adafruit_LittleFS_Namespace::FILE_O_READ;
using Adafruit_LittleFS_Namespace::FILE_O_WRITE;

const char* const kSlots[] = {"/deskcfg0.bin", "/deskcfg1.bin"};
constexpr uint16_t kSize = 240, kCrcOffset = 236;
enum class Error {None,InvalidBank,Mount,SlotRemove,SlotOpen,SlotWrite,SlotVerify};
Error saveError = Error::None;

bool fail(Error error) { saveError = error; return false; }

// DBCF[4], version:u16, size:u16, sequence:u32, bank[224], CRC32:u32.
// Shares the mounted filesystem but never modifies UI, calibration or bonds.
struct Record { uint32_t sequence; deskconfig::Bank bank; };

bool mountWithoutFormatting() {
  // BSP derived InternalFS.begin() can format on failure. Call only the base
  // mount: initialization/recovery of an unmountable filesystem is not ours.
  return InternalFS.Adafruit_LittleFS::begin();
}
uint16_t read16(const uint8_t* p) {
  return static_cast<uint16_t>(p[0]) | (static_cast<uint16_t>(p[1]) << 8);
}
uint32_t read32(const uint8_t* p) {
  return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) |
         (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
}
void write16(uint8_t* p, uint16_t value) {
  p[0] = static_cast<uint8_t>(value); p[1] = static_cast<uint8_t>(value >> 8);
}
void write32(uint8_t* p, uint32_t value) {
  for (uint8_t i = 0; i < 4; ++i) p[i] = static_cast<uint8_t>(value >> (8u * i));
}
uint32_t crc32(const uint8_t* data, uint16_t length) {
  uint32_t crc = 0xFFFFFFFFu;
  for (uint16_t i = 0; i < length; ++i) {
    crc ^= data[i];
    for (uint8_t bit = 0; bit < 8; ++bit)
      crc = (crc >> 1) ^ ((crc & 1u) ? 0xEDB88320u : 0u);
  }
  return crc ^ 0xFFFFFFFFu;
}
void encode(const Record& record, uint8_t* bytes) {
  memset(bytes, 0, kSize); memcpy(bytes, "DBCF", 4);
  write16(bytes + 4, 1); write16(bytes + 6, kSize);
  write32(bytes + 8, record.sequence); memcpy(bytes + 12, record.bank.bytes, deskconfig::BankSize);
  write32(bytes + kCrcOffset, crc32(bytes, kCrcOffset));
}
bool decode(const uint8_t* bytes, Record& result) {
  if (memcmp(bytes, "DBCF", 4) || read16(bytes + 4) != 1 ||
      read16(bytes + 6) != kSize ||
      read32(bytes + kCrcOffset) != crc32(bytes, kCrcOffset)) return false;
  deskconfig::Bank bank; memcpy(bank.bytes, bytes + 12, deskconfig::BankSize);
  if (!deskconfig::validate(bank)) return false;
  result.sequence = read32(bytes + 8); result.bank = bank;
  return true;
}
bool readRecord(const char* path, Record& result) {
  File file(InternalFS);
  if (!file.open(path, FILE_O_READ)) return false;
  uint8_t bytes[kSize];
  const bool complete = !file.isDirectory() && file.size() == kSize &&
                        file.read(bytes, kSize) == kSize;
  file.close();
  return complete && decode(bytes, result);
}
int latestSlot(const Record* records, const bool* valid) {
  if (!valid[0]) return valid[1] ? 1 : -1;
  if (!valid[1]) return 0;
  const uint32_t delta = records[1].sequence - records[0].sequence;
  // Adjacent saves differ by one. Equal/half-range ties choose slot zero.
  return delta != 0 && delta < 0x80000000u ? 1 : 0;
}
bool removeIfPresent(const char* path) {
  return !InternalFS.exists(path) || InternalFS.remove(path);
}
bool sameRecord(const Record& a, const Record& b) {
  return a.sequence == b.sequence && deskconfig::same(a.bank, b.bank);
}

}  // namespace

const char* lastErrorName() {
  switch (saveError) {
    case Error::None: return "none";
    case Error::InvalidBank: return "invalid_bank";
    case Error::Mount: return "mount";
    case Error::SlotRemove: return "slot_remove";
    case Error::SlotOpen: return "slot_open";
    case Error::SlotWrite: return "slot_write";
    case Error::SlotVerify: return "slot_verify";
  }
  return "unknown";
}

bool load(deskconfig::Bank& bank) {
  if (!mountWithoutFormatting()) return false;
  Record records[2] = {};
  const bool valid[] = {readRecord(kSlots[0], records[0]),
                        readRecord(kSlots[1], records[1])};
  const int latest = latestSlot(records, valid);
  if (latest < 0) return false;
  bank = records[latest].bank;
  return true;
}

bool save(const deskconfig::Bank& bank) {
  saveError = Error::None;
  if (!deskconfig::validate(bank)) return fail(Error::InvalidBank);
  if (!mountWithoutFormatting()) return fail(Error::Mount);
  Record records[2] = {};
  const bool valid[] = {readRecord(kSlots[0], records[0]),
                        readRecord(kSlots[1], records[1])};
  const int latest = latestSlot(records, valid);
  if (latest >= 0 && deskconfig::same(records[latest].bank, bank)) return true;
  const int target = latest == 0 ? 1 : 0;
  const Record candidate = {latest < 0 ? 1u : records[latest].sequence + 1u, bank};
  uint8_t bytes[kSize]; encode(candidate, bytes);

  // BSP FILE_O_WRITE appends: remove only the non-latest slot before writing.
  // Keep the newest valid record intact through every checked failure. Avoid
  // BSP 1.7.0's littlefs rename path: splitting its 128-byte directory can make
  // rename report success without publishing the destination. No rename is
  // needed with two independently validated records; legacy temp is ignored.
  if (!removeIfPresent(kSlots[target])) return fail(Error::SlotRemove);
  File file(InternalFS);
  if (!file.open(kSlots[target], FILE_O_WRITE)) return fail(Error::SlotOpen);
  const bool complete = file.write(bytes, kSize) == kSize;
  file.flush(); file.close();
  Record verified = {};
  if (!complete) return fail(Error::SlotWrite);
  if (!readRecord(kSlots[target], verified) || !sameRecord(candidate, verified))
    return fail(Error::SlotVerify);
  return true;
}

}  // namespace deskconfigstore
