/*
 * Tiny bare-metal "OS" for the firmware-managed I3C slave.
 *
 * It drives the invented I3C target peripheral (I3C.InventedI3CTarget): it polls the RX FIFO for
 * bytes sent by the I3C master, echoes them into the TX FIFO, and commits the response - which the
 * target turns into an In-Band Interrupt carrying the bytes back to the master (and, through the TCP
 * bridge, back to the external client).
 */
#include <stdint.h>

#define UART_BASE       0x70001000u
#define UART_TX         (*(volatile uint8_t  *)(UART_BASE + 0x00))

#define I3C_BASE        0x90000000u
#define I3C_RX_STATUS   (*(volatile uint32_t *)(I3C_BASE + 0x00)) /* bit0 = data available */
#define I3C_RX_DATA     (*(volatile uint32_t *)(I3C_BASE + 0x04)) /* pop one RX byte */
#define I3C_TX_DATA     (*(volatile uint32_t *)(I3C_BASE + 0x08)) /* push one TX byte */
#define I3C_TX_COMMIT   (*(volatile uint32_t *)(I3C_BASE + 0x0C)) /* finalise the response */

static void uart_puts(const char *s)
{
    while(*s)
    {
        UART_TX = (uint8_t)*s++;
    }
}

int main(void)
{
    uart_puts("i3c-firmware: ready\n");

    for(;;)
    {
        if(I3C_RX_STATUS & 1u)
        {
            /* Drain every byte currently available and echo it into the TX FIFO. */
            while(I3C_RX_STATUS & 1u)
            {
                uint8_t b = (uint8_t)I3C_RX_DATA;
                I3C_TX_DATA = b;
            }
            /* Hand the response back to the master via an In-Band Interrupt. */
            I3C_TX_COMMIT = 1u;
            uart_puts("i3c-firmware: echoed a message\n");
        }
    }

    return 0;
}
