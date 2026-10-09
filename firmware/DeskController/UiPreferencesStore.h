#ifndef DESK_UI_PREFERENCES_STORE_H
#define DESK_UI_PREFERENCES_STORE_H

#include <stdint.h>

namespace deskprefs {

enum class Error : uint8_t {
  None, InvalidLanguage, Mount, SlotRemove, SlotOpen, SlotWrite, SlotVerify
};

// Outcome of the most recent save(), never overwritten by load(). Stage names
// identify where the checked operation failed, not a diagnosed flash cause.
Error lastError();
const char* lastErrorName();

// Language 0 = Chinese, 1 = English. The caller chooses its default before
// load(); false leaves that value unchanged. No reads format or write storage.
bool load(uint8_t& language);

// Single language-save worker only, after an explicit choice; not an ISR API.
// Identical valid preferences do not write flash. Success means a record was
// read back and verified, not that physical power-loss testing has occurred.
// Only /deskui0.bin and /deskui1.bin may be modified; the newest valid slot is
// kept while writing its replacement. Legacy /deskui.tmp is ignored. Calibration,
// BLE bonds and other files are left alone. Mount failures never format FS.
bool save(uint8_t language);

}  // namespace deskprefs
#endif
