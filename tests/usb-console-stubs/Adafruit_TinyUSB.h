#pragma once
#include <stdint.h>
bool tud_cdc_n_connected(uint8_t);
uint32_t tud_cdc_n_write_available(uint8_t);
uint32_t tud_cdc_n_write(uint8_t,const void*,uint32_t);
uint32_t tud_cdc_n_write_flush(uint8_t);
bool tud_cdc_n_write_clear(uint8_t);
uint32_t tud_cdc_n_read(uint8_t,void*,uint32_t);
extern "C" void tud_cdc_rx_cb(uint8_t);
extern "C" void tud_event_hook_cb(uint8_t,uint32_t,bool);
