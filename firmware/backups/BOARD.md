# Board identity

| Field | Value |
|---|---|
| Date checked | 2026-09-10 |
| QMK Toolbox bootloader line | STM32Duino device connected (WinUSB): Maple 003 (1EAF:0003:0201) |
| Revision | **rev2** (APM32/STM32, stm32duino bootloader) |
| Normal USB | VID `0xD010` PID `0x1601` |
| Current-image dump | `kb16-rev2-current-dump-20260910.bin` (122880 bytes, SHA256 CA62245DB21A17A1A369C8FC2A5EB8FD4A346B06C50D15A4562D7B72478A5BDD) |
| Secondary known-good | `kb16-rev2-community-vial-thompson.bin` (community Vial build — may differ from what's on the pad) |
| Flash target | `doio/kb16/rev2` |
| Notes | Dump via `dfu-util -a 2 -U` while in Maple bootloader. Exit code 74 / LIBUSB_ERROR_PIPE at end is common; size matches 120 KiB app region. Do not flash rev1 images. |
