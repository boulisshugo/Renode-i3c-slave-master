/*
 * Tiny bare-metal "OS" for the firmware-managed SPI slave.
 *
 * It drives the invented SPI target peripheral (SPI.InventedSPITarget): it polls the RX FIFO for
 * bytes clocked in by the SPI controller, echoes them into the TX FIFO, and commits the response -
 * which the target turns into an interrupt carrying the bytes back to the controller (and, through
 * the TCP bridge, back to the external client).
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
    uart_puts("spi-firmware: ready\n");

    for(;;)
    {
        if(SPI_RX_STATUS & 1u)
        {
            /* Drain every byte currently available and echo it into the TX FIFO. */
            while(SPI_RX_STATUS & 1u)
            {
                uint8_t b = (uint8_t)SPI_RX_DATA;
                SPI_TX_DATA = b;
            }
            /* Hand the response back to the controller via the interrupt line. */
            SPI_TX_COMMIT = 1u;
            uart_puts("spi-firmware: echoed a message\n");
        }
    }

    return 0;
}
