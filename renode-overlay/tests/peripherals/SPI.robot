*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/SPI-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33667

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SPI.repl

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# Full-duplex transfer: one byte out, one byte in, per clock
# --------------------------------------------------------------------------------------------------
Should Exchange Data Full Duplex
    Create Machine

    Execute Command             spi.slave0 EnqueueResponseBytesHex "01020304"

    ${miso}=                    Execute Command  spi TransferHex 0 "DEADBEEF"
    Should Contain              ${miso}  [0x1, 0x2, 0x3, 0x4]

    ${rx}=                      Execute Command  spi.slave0 LastReceivedHex
    Should Contain              ${rx}  [0xDE, 0xAD, 0xBE, 0xEF]

Should Shift Out Zeros When No Response Queued
    Create Machine

    ${miso}=                    Execute Command  spi TransferHex 0 "AABB"
    Should Contain              ${miso}  [0x0, 0x0]

# --------------------------------------------------------------------------------------------------
# Chip-select selects exactly one target
# --------------------------------------------------------------------------------------------------
Should Isolate Transfer To The Addressed Chip Select
    Create Machine

    Execute Command             spi TransferHex 0 "AA"

    ${rx1}=                     Execute Command  spi.slave1 LastReceivedHex
    Should Contain              ${rx1}  []

# --------------------------------------------------------------------------------------------------
# A missing chip select must warn, not crash
# --------------------------------------------------------------------------------------------------
Should Warn On Access To Missing Chip Select
    Create Machine
    Create Log Tester           1

    Execute Command             spi TransferHex 7 "AB"
    Wait For Log Entry          No SPI target registered at chip select 7

# --------------------------------------------------------------------------------------------------
# Data-ready interrupt (SPI side-band, the analog of an I3C IBI)
# --------------------------------------------------------------------------------------------------
Should Capture Interrupt
    Create Machine

    ${irq}=                     Execute Command  spi IRQ IsSet
    Should Be Equal             ${irq.strip()}  False

    Execute Command             spi.slave1 RequestInterrupt

    ${irq}=                     Execute Command  spi IRQ IsSet
    Should Be Equal             ${irq.strip()}  True
    ${cs}=                      Execute Command  spi LastInterruptChipSelect
    Should Be Equal As Numbers  ${cs}  1

Should Carry Data In An Interrupt
    Create Machine

    Execute Command             spi.slave0 RequestInterruptWithData "112233"
    ${payload}=                 Execute Command  spi LastInterruptPayloadHex
    Should Contain              ${payload}  [0x11, 0x22, 0x33]

Should Acknowledge Interrupt
    Create Machine

    Execute Command             spi.slave1 RequestInterrupt
    ${irq}=                     Execute Command  spi IRQ IsSet
    Should Be Equal             ${irq.strip()}  True

    Execute Command             spi AcknowledgeInterrupt
    ${irq}=                     Execute Command  spi IRQ IsSet
    Should Be Equal             ${irq.strip()}  False

# --------------------------------------------------------------------------------------------------
# TCP bridge round-trip
# --------------------------------------------------------------------------------------------------
Should Bridge Raw Data Over TCP
    Create Machine
    Execute Command             emulation CreateSPITCPBridge sysbus.spi 0 ${BRIDGE_PORT}

    # Queue the bytes the target shifts back on MISO during the exchange.
    Execute Command             spi.slave0 EnqueueResponseBytesHex "01020304"

    ${response}=                Transfer Over Spi Bridge  ${BRIDGE_PORT}  DEADBEEF
    Should Be Equal             ${response}  01020304

    ${rx}=                      Execute Command  spi.slave0 LastReceivedHex
    Should Contain              ${rx}  [0xDE, 0xAD, 0xBE, 0xEF]
