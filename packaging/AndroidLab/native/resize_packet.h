/* SonicRoute owned-display control extension. SPDX-License-Identifier: Apache-2.0 */
#ifndef SONICROUTE_RESIZE_PACKET_H
#define SONICROUTE_RESIZE_PACKET_H
#include <stddef.h>
#include <stdint.h>
#define SR_RESIZE_CONTROL_TYPE 18
static inline size_t sr_resize_serialize(uint8_t *buffer, uint16_t width, uint16_t height) {
    buffer[0] = SR_RESIZE_CONTROL_TYPE;
    buffer[1] = width >> 8; buffer[2] = width & 255;
    buffer[3] = height >> 8; buffer[4] = height & 255;
    return 5;
}
#endif
