*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/SPI-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33673

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SPI-sehal.repl

Receive Should Be Done
    ${v}=                       Execute Command  spi ReceiveInProgress
    Should Contain              ${v}  False

*** Test Cases ***
# The clock-driven poll controller: Transfer() sends synchronously, then the LimitTimer polls the SE in
# virtual time until it returns a non-0xFF NAD, reads the length, and assembles the [NAD, PCB, LEN, body]
# block. Deterministic - it runs on the clock-source thread, so it only completes while the emulation runs.
Should Send Then Poll And Assemble The Response Block
    Create Machine
    Execute Command             spi.se CommandLength 4
    Execute Command             spi.se NotReadyPolls 3
    # NAD=21, PCB=00, LEN=03, then 3 bytes of payload+CRC (90 00 AB).
    Execute Command             spi.se SetResponseBlockHex "2100039000AB"

    # Send phase is synchronous; arms the poll timer.
    ${sent}=                    Execute Command  spi TransferHex 0 "00A40000"
    ${prog}=                    Execute Command  spi ReceiveInProgress
    Should Contain              ${prog}  True

    Start Emulation
    Wait Until Keyword Succeeds  20x  0.1s  Receive Should Be Done

    # The controller captured the whole framed block...
    ${block}=                   Execute Command  spi LastReceivedBlockHex
    Should Contain              ${block}  [0x21, 0x0, 0x3, 0x90, 0x0, 0xAB]

    # ...and the SE saw exactly the 4 command bytes during the send phase.
    ${cmd}=                     Execute Command  spi.se ReceivedCommandHex
    Should Contain              ${cmd}  [0x0, 0xA4, 0x0, 0x0]

# With NotReadyPolls = 0 the SE answers on the first poll clock - the fast path.
Should Handle An Immediate Response
    Create Machine
    Execute Command             spi.se CommandLength 2
    Execute Command             spi.se NotReadyPolls 0
    Execute Command             spi.se SetResponseBlockHex "A50001FF"

    Execute Command             spi TransferHex 0 "ABCD"
    Start Emulation
    Wait Until Keyword Succeeds  20x  0.1s  Receive Should Be Done

    ${block}=                   Execute Command  spi LastReceivedBlockHex
    Should Contain              ${block}  [0xA5, 0x0, 0x1, 0xFF]

# A second Transfer while a receive is still pending is rejected, not corrupted.
Should Reject A Transfer While A Receive Is In Progress
    Create Machine
    Execute Command             spi.se CommandLength 1
    Execute Command             spi.se NotReadyPolls 5
    Execute Command             spi.se SetResponseBlockHex "3300021234"

    Execute Command             spi TransferHex 0 "11"
    # Timer not yet ticking (emulation paused) - the first receive is still in progress.
    ${second}=                  Execute Command  spi TransferHex 0 "22"
    Should Contain              ${second}  []

    Start Emulation
    Wait Until Keyword Succeeds  20x  0.1s  Receive Should Be Done
    ${block}=                   Execute Command  spi LastReceivedBlockHex
    Should Contain              ${block}  [0x33, 0x0, 0x2, 0x12, 0x34]

# End-to-end over TCP: the client sends the raw command and receives ONLY the raw response block
# (NAD + PCB + LEN + payload/CRC) - no idle bytes, no extra framing added by the bridge.
Should Deliver Only The Raw Block To A TCP Client
    Create Machine
    Execute Command             spi.se CommandLength 5
    Execute Command             spi.se NotReadyPolls 3
    Execute Command             spi.se SetResponseBlockHex "2100039000AB"
    # The bridge auto-detects the SpiControllerSeHal and forwards its BlockReceived event.
    Execute Command             emulation CreateSPITCPBridge sysbus.spi 0 ${BRIDGE_PORT}
    Start Emulation

    # Send a 5-byte command; expect exactly the 6-byte block back.
    ${response}=                Transfer Over Spi Bridge  ${BRIDGE_PORT}  0011223344  6  timeout=5
    Should Be Equal             ${response}  2100039000ab
