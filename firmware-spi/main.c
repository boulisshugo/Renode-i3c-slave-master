/*
 * Tiny bare-metal "OS" for the firmware-managed SPI slave.
 *
 * It drives the invented SPI target peripheral (SPI.InventedSPITarget). SPI slaves cannot push, so the
 * response is delivered by having the master poll: the firmware reads the command from the RX FIFO,
 * then writes a length-prefixed response into the TX FIFO ([N, byte0..byteN-1]) and commits it. The
 * master then clocks a status byte until it reads N (ready), and clocks out the N response bytes.
 */
#include <stdint.h>

#define UART_BASE       0x70001000u
#define UART_TX         (*(volatile uint8_t  *)(UART_BASE + 0x00))

#define SPI_BASE        0x90000000u
#define SPI_RX_STATUS   (*(volatile uint32_t *)(SPI_BASE + 0x00)) /* bit0 = data available */
#define SPI_RX_DATA     (*(volatile uint32_t *)(SPI_BASE + 0x04)) /* pop one RX byte */
#define SPI_TX_DATA     (*(volatile uint32_t *)(SPI_BASE + 0x08)) /* push one TX byte */
#define SPI_TX_COMMIT   (*(volatile uint32_t *)(SPI_BASE + 0x0C)) /* finalise the response */

static void uart_puts(const char *s)
{
    while(*s)
    {
        UART_TX = (uint8_t)*s++;
    }
}

int main(void)
{
    static uint8_t buf[256];

    uart_puts("spi-firmware: ready\n");

    for(;;)
    {
        if(SPI_RX_STATUS & 1u)
        {
            /* Drain the whole command from the RX FIFO. */
            uint32_t n = 0;
            while((SPI_RX_STATUS & 1u) && n < sizeof(buf))
            {
                buf[n++] = (uint8_t)SPI_RX_DATA;
            }
            /* Write a length-prefixed echo response and commit it for the master to poll. */
            SPI_TX_DATA = (uint8_t)n;
            for(uint32_t i = 0; i < n; i++)
            {
                SPI_TX_DATA = buf[i];
            }
            SPI_TX_COMMIT = 1u;
            uart_puts("spi-firmware: echoed a message\n");
        }
    }

    return 0;
}
