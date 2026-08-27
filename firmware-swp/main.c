/*
 * Tiny bare-metal "OS" for a firmware-managed SWP (ETSI TS 102 613) UICC.
 *
 * THE POINT OF THIS FILE
 *
 * The SWP protocol layers live HERE, not in the peripheral model. SWP.InventedSWPTarget is a
 * transceiver: it frames, bit-stuffs and CRCs what this firmware hands it, and hands back the LLC
 * payloads it receives. Every ACT_SYNC, ACT_READY, UA, RR and I-frame the CLF sees on the wire is
 * built by the code below, running on the emulated CPU. If this file stops answering, the CLF hears
 * silence - which is exactly what would happen on a bench, and the reason the split is worth having.
 *
 * WHAT IT DOES
 *
 *   ACT LLC (clause 11)     ACT_EVT interrupt -> open the LLC and send ACT_SYNC with our
 *                           ACT_INFORMATION; on ACT_POWER_MODE answer ACT_READY, or repeat the last
 *                           ACT frame when the CLF sets the frame-resend (FR) bit.
 *   SHDLC LLC (clause 10)   RSET -> UA with the negotiated window; modulo-8 N(S)/N(R); an I-frame is
 *                           answered with an I-frame carrying the application's response (the
 *                           acknowledgement rides in its N(R)), or with a bare RR when there is
 *                           nothing to say; an out-of-sequence I-frame draws a REJ.
 *   Deactivation            DEACT_EVT interrupt -> close the LLC and drop every scrap of state.
 *
 * The application layer is a deliberate placeholder: it reverses the request. Replace swp_app() with
 * the real thing; nothing else here needs to change.
 *
 * It polls STATUS rather than taking the interrupt, so the platform needs no interrupt controller.
 * The peripheral's IRQ line carries the same three sources (ACT_EVT, DEACT_EVT, RX_FRAME) and can be
 * wired to one in the .repl if the firmware under test is interrupt-driven.
 */
#include <stdint.h>

#define UART_BASE           0x70001000u
#define UART_TX             (*(volatile uint8_t  *)(UART_BASE + 0x00))

/* SWP.InventedSWPTarget register window. */
#define SWP_BASE            0x90000000u
#define SWP_STATUS          (*(volatile uint32_t *)(SWP_BASE + 0x00))
#define SWP_STATUS_CLEAR    (*(volatile uint32_t *)(SWP_BASE + 0x04))
#define SWP_IRQ_ENABLE      (*(volatile uint32_t *)(SWP_BASE + 0x08))
#define SWP_RX_DATA         (*(volatile uint32_t *)(SWP_BASE + 0x0C))
#define SWP_RX_NEXT         (*(volatile uint32_t *)(SWP_BASE + 0x10))
#define SWP_TX_DATA         (*(volatile uint32_t *)(SWP_BASE + 0x14))
#define SWP_TX_COMMIT       (*(volatile uint32_t *)(SWP_BASE + 0x18))
#define SWP_CONTROL         (*(volatile uint32_t *)(SWP_BASE + 0x1C))
#define SWP_LLC_STATE       (*(volatile uint32_t *)(SWP_BASE + 0x20))

#define SWP_STAT_ACT_EVT    (1u << 0)   /* the CLF activated the interface */
#define SWP_STAT_DEACT_EVT  (1u << 1)   /* the CLF deactivated it */
#define SWP_STAT_RX_FRAME   (1u << 2)   /* a complete LLC payload is waiting */
#define SWP_STAT_POWERED    (1u << 3)   /* S1 is driven */
#define SWP_STAT_RX_COUNT(s) (((s) >> 8) & 0xFFFFu)

/* LLC states published for the monitor and the test suites (introspection only). */
#define SWP_LLC_CLOSED          0u
#define SWP_LLC_OPENED          1u
#define SWP_LLC_ACT_SYNC_SENT   2u
#define SWP_LLC_ACT_READY_SENT  3u
#define SWP_LLC_ESTABLISHED     4u

/* ACT LLC control fields (clause 11). */
#define ACT_SYNC            0x01u
#define ACT_POWER_MODE      0x02u
#define ACT_READY           0x03u
#define ACT_PM_FULL_POWER   0x01u
#define ACT_PM_FRAME_RESEND 0x02u

/* SHDLC control byte (clause 10): I '80'/'A0', S 'C0', U 'E0'. */
#define SHDLC_HEAD_MASK     0xE0u
#define SHDLC_HEAD_I        0x80u
#define SHDLC_HEAD_I2       0xA0u
#define SHDLC_HEAD_S        0xC0u
#define SHDLC_HEAD_U        0xE0u
#define SHDLC_NS(c)         (((c) >> 3) & 0x07u)
#define SHDLC_NR(c)         ((c) & 0x07u)
#define SHDLC_S_TYPE(c)     (((c) >> 3) & 0x03u)
#define SHDLC_S_RR          0x00u
#define SHDLC_S_REJ         0x01u
#define SHDLC_U_MOD(c)      ((c) & 0x1Fu)
#define SHDLC_U_UA          0x06u
#define SHDLC_U_RSET        0x19u

/* What this UICC advertises in ACT_INFORMATION. */
#define UICC_VERSION        1u
#define UICC_LLCS           0x05u   /* SHDLC | ACT */
#define UICC_MAX_FRAME      256u
#define UICC_POWER_MODES    0x03u   /* low power | full power */
#define UICC_MAX_WINDOW     4u

#define MAX_PAYLOAD         272u

static uint8_t  rx[MAX_PAYLOAD];
static uint8_t  tx[MAX_PAYLOAD];
static uint8_t  last_act[8];
static uint32_t last_act_len;

static uint32_t send_seq;       /* N(S) of the next I-frame we send   */
static uint32_t recv_seq;       /* N(S) we expect from the CLF next   */
static uint32_t link_up;
static uint32_t window;
static uint32_t full_power;

static void uart_puts(const char *s)
{
    while(*s)
    {
        UART_TX = (uint8_t)*s++;
    }
}

static void llc_state(uint32_t state)
{
    SWP_LLC_STATE = state;
}

/* Hands one complete LLC payload - control field first - to the transceiver, which frames it, adds
 * the CRC and drives it onto S2. Nothing leaves this chip that does not come through here. */
static void swp_send(const uint8_t *payload, uint32_t length)
{
    for(uint32_t i = 0; i < length; i++)
    {
        SWP_TX_DATA = payload[i];
    }
    SWP_TX_COMMIT = 1u;
}

/* ACT frames are the ones the CLF can ask us to repeat (FR = 1), so keep the last one. */
static void act_send(const uint8_t *payload, uint32_t length)
{
    for(uint32_t i = 0; i < length && i < sizeof(last_act); i++)
    {
        last_act[i] = payload[i];
    }
    last_act_len = length;
    swp_send(payload, length);
}

static void link_reset(void)
{
    send_seq = 0;
    recv_seq = 0;
    link_up = 0;
    window = UICC_MAX_WINDOW;
    last_act_len = 0;
}

/* ACT_EVT: the CLF has activated the interface. Open the LLC and announce ourselves. */
static void llc_open(void)
{
    uint8_t sync[6];

    link_reset();
    llc_state(SWP_LLC_OPENED);

    sync[0] = ACT_SYNC;
    sync[1] = UICC_VERSION;
    sync[2] = UICC_LLCS;
    sync[3] = (uint8_t)(UICC_MAX_FRAME >> 8);
    sync[4] = (uint8_t)(UICC_MAX_FRAME & 0xFFu);
    sync[5] = UICC_POWER_MODES;
    act_send(sync, sizeof(sync));

    llc_state(SWP_LLC_ACT_SYNC_SENT);
    uart_puts("swp-firmware: ACT_SYNC sent\n");
}

/* DEACT_EVT: S1 is gone. Close both layers and keep nothing. */
static void llc_close(void)
{
    link_reset();
    llc_state(SWP_LLC_CLOSED);
    uart_puts("swp-firmware: interface deactivated\n");
}

/* The application layer above SHDLC. Returns the response length, 0 for "nothing to say" (which
 * becomes a bare RR on the wire). Replace this with the real application. */
static uint32_t swp_app(const uint8_t *request, uint32_t length, uint8_t *response)
{
    for(uint32_t i = 0; i < length; i++)
    {
        response[i] = request[length - 1 - i];
    }
    return length;
}

static void act_receive(const uint8_t *payload, uint32_t length)
{
    uint8_t parameter;
    uint8_t ready;

    if(length == 0 || payload[0] != ACT_POWER_MODE)
    {
        return;
    }

    parameter = length > 1 ? payload[1] : 0u;
    if(parameter & ACT_PM_FRAME_RESEND)
    {
        /* The CLF did not get our last ACT frame intact - send it again, byte for byte. */
        if(last_act_len != 0u)
        {
            swp_send(last_act, last_act_len);
        }
        uart_puts("swp-firmware: FR set, ACT frame repeated\n");
        return;
    }

    full_power = (parameter & ACT_PM_FULL_POWER) ? 1u : 0u;
    ready = ACT_READY;
    act_send(&ready, 1u);
    llc_state(SWP_LLC_ACT_READY_SENT);
    uart_puts(full_power ? "swp-firmware: ACT_READY sent (full power)\n"
                         : "swp-firmware: ACT_READY sent (low power)\n");
}

static void shdlc_receive(const uint8_t *payload, uint32_t length)
{
    uint8_t control = payload[0];
    uint8_t head = control & SHDLC_HEAD_MASK;

    if(head == SHDLC_HEAD_U)
    {
        if(SHDLC_U_MOD(control) != SHDLC_U_RSET)
        {
            return;
        }
        /* RSET: restart the link, accepting the smaller of the two proposed windows. */
        link_reset();
        window = (length > 1 && payload[1] < UICC_MAX_WINDOW) ? payload[1] : UICC_MAX_WINDOW;
        if(window == 0u)
        {
            window = 1u;
        }
        link_up = 1u;
        tx[0] = (uint8_t)(SHDLC_HEAD_U | SHDLC_U_UA);
        tx[1] = (uint8_t)window;
        tx[2] = 0u; /* no selective reject */
        swp_send(tx, 3u);
        llc_state(SWP_LLC_ESTABLISHED);
        uart_puts("swp-firmware: SHDLC link established\n");
        return;
    }

    if(head == SHDLC_HEAD_S)
    {
        /* RR acknowledges; REJ would ask for a retransmission we do not buffer in this demo. */
        return;
    }

    if(head != SHDLC_HEAD_I && head != SHDLC_HEAD_I2)
    {
        return;
    }
    if(!link_up)
    {
        return;
    }

    if(SHDLC_NS(control) != recv_seq)
    {
        /* Out of sequence: ask for the frame we do expect. */
        tx[0] = (uint8_t)(SHDLC_HEAD_S | (SHDLC_S_REJ << 3) | recv_seq);
        swp_send(tx, 1u);
        uart_puts("swp-firmware: out-of-sequence I-frame, REJ sent\n");
        return;
    }
    recv_seq = (recv_seq + 1u) & 0x07u;

    {
        uint32_t answer = swp_app(payload + 1, length - 1u, tx + 1);
        if(answer == 0u)
        {
            /* Nothing to say: a bare RR carrying our updated N(R). */
            tx[0] = (uint8_t)(SHDLC_HEAD_S | (SHDLC_S_RR << 3) | recv_seq);
            swp_send(tx, 1u);
            return;
        }
        /* Piggyback the acknowledgement on our own I-frame. */
        tx[0] = (uint8_t)(SHDLC_HEAD_I | (send_seq << 3) | recv_seq);
        send_seq = (send_seq + 1u) & 0x07u;
        swp_send(tx, answer + 1u);
    }
}

/* Reads one complete LLC payload out of the RX FIFO. The byte count in STATUS is the frame
 * boundary: draining exactly that many bytes is what keeps two frames from running together. */
static uint32_t swp_receive(uint32_t count)
{
    uint32_t i;

    if(count > MAX_PAYLOAD)
    {
        /* Longer than we advertised we could take - drop it rather than overrun the buffer. */
        SWP_RX_NEXT = 1u;
        uart_puts("swp-firmware: oversized frame dropped\n");
        return 0u;
    }
    for(i = 0; i < count; i++)
    {
        rx[i] = (uint8_t)SWP_RX_DATA;
    }
    return count;
}

int main(void)
{
    uint32_t status;
    uint32_t count;

    link_reset();
    llc_state(SWP_LLC_CLOSED);
    SWP_IRQ_ENABLE = SWP_STAT_ACT_EVT | SWP_STAT_DEACT_EVT | SWP_STAT_RX_FRAME;

    uart_puts("swp-firmware: ready\n");

    for(;;)
    {
        status = SWP_STATUS;

        if(status & SWP_STAT_ACT_EVT)
        {
            SWP_STATUS_CLEAR = SWP_STAT_ACT_EVT;
            llc_open();
            continue;
        }
        if(status & SWP_STAT_DEACT_EVT)
        {
            SWP_STATUS_CLEAR = SWP_STAT_DEACT_EVT;
            llc_close();
            continue;
        }
        if(!(status & SWP_STAT_RX_FRAME))
        {
            continue;
        }

        count = swp_receive(SWP_STAT_RX_COUNT(status));
        if(count == 0u)
        {
            continue;
        }

        /* Which LLC the control byte belongs to follows from the state we are in: everything below
         * '80' is ACT, and SHDLC owns '80'..'FF'. */
        if(rx[0] < SHDLC_HEAD_I)
        {
            act_receive(rx, count);
        }
        else
        {
            shdlc_receive(rx, count);
        }
    }

    return 0;
}
