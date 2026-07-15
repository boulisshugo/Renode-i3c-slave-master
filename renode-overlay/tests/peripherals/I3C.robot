*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/I3C-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33567

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/I3C.repl

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# Enumeration identifiers: Provisioned ID, BCR, DCR, static address
# --------------------------------------------------------------------------------------------------
Should Expose Enumeration Identifiers
    Create Machine

    ${pid}=                     Execute Command  i3c.slave0 ProvisionedId
    Should Be Equal As Numbers  ${pid}  0x1234567890AB
    ${bcr}=                     Execute Command  i3c.slave0 BusCharacteristics
    Should Be Equal As Numbers  ${bcr}  0x02
    ${dcr}=                     Execute Command  i3c.slave0 DeviceCharacteristics
    Should Be Equal As Numbers  ${dcr}  0xC5
    ${static}=                  Execute Command  i3c.slave0 StaticAddress
    Should Be Equal As Numbers  ${static}  0x50

# --------------------------------------------------------------------------------------------------
# Dynamic addressing (simplified ENTDAA)
# --------------------------------------------------------------------------------------------------
Should Start With No Dynamic Address
    Create Machine

    ${addr0}=                   Execute Command  i3c.slave0 DynamicAddress
    Should Be Equal As Numbers  ${addr0}  0x00
    ${addr1}=                   Execute Command  i3c.slave1 DynamicAddress
    Should Be Equal As Numbers  ${addr1}  0x00

Should Assign Dynamic Addresses
    Create Machine

    Execute Command             i3c AssignDynamicAddresses

    ${addr0}=                   Execute Command  i3c.slave0 DynamicAddress
    Should Be Equal As Numbers  ${addr0}  0x08
    ${addr1}=                   Execute Command  i3c.slave1 DynamicAddress
    Should Be Equal As Numbers  ${addr1}  0x09

Should Clear Dynamic Address On Reset
    Create Machine

    Execute Command             i3c AssignDynamicAddresses
    Execute Command             i3c.slave0 Reset
    ${addr0}=                   Execute Command  i3c.slave0 DynamicAddress
    Should Be Equal As Numbers  ${addr0}  0x00

# --------------------------------------------------------------------------------------------------
# SDR private write
# --------------------------------------------------------------------------------------------------
Should Perform Private Write
    Create Machine

    Execute Command             i3c WritePrivateHex 0x08 "DEADBEEF"

    ${rx}=                      Execute Command  i3c.slave0 LastReceivedHex
    Should Contain              ${rx}  [0xDE, 0xAD, 0xBE, 0xEF]

Should Isolate Private Write To The Addressed Target
    Create Machine

    Execute Command             i3c WritePrivateHex 0x08 "AA"

    ${rx1}=                     Execute Command  i3c.slave1 LastReceivedHex
    Should Contain              ${rx1}  []

# --------------------------------------------------------------------------------------------------
# SDR private read
# --------------------------------------------------------------------------------------------------
Should Perform Private Read
    Create Machine

    Execute Command             i3c.slave0 EnqueueResponseBytesHex "0102A0"

    ${rx}=                      Execute Command  i3c ReadPrivateHex 0x08 3
    Should Contain              ${rx}  [0x1, 0x2, 0xA0]

Should Pad Private Read Beyond Queued Data With Zeros
    Create Machine

    Execute Command             i3c.slave0 EnqueueResponseBytesHex "AB"

    ${rx}=                      Execute Command  i3c ReadPrivateHex 0x08 3
    Should Contain              ${rx}  [0xAB, 0x0, 0x0]

# --------------------------------------------------------------------------------------------------
# Access to a non-existent target must warn, not crash
# --------------------------------------------------------------------------------------------------
Should Warn On Access To Missing Target
    Create Machine
    Create Log Tester           1

    Execute Command             i3c WritePrivateHex 0x7F "AB"
    Wait For Log Entry          No I3C target registered at address 0x7F

# --------------------------------------------------------------------------------------------------
# Common Command Codes
# --------------------------------------------------------------------------------------------------
Should Deliver Broadcast CCC To All Targets
    Create Machine

    Execute Command             i3c SendBroadcastCommandCode 0x06
    ${ccc0}=                    Execute Command  i3c.slave0 LastCommandCode
    Should Be Equal As Numbers  ${ccc0}  0x06
    ${ccc1}=                    Execute Command  i3c.slave1 LastCommandCode
    Should Be Equal As Numbers  ${ccc1}  0x06

Should Deliver Direct CCC Only To Addressed Target
    Create Machine

    Execute Command             i3c SendDirectCommandCode 0x80 0x09
    ${ccc1}=                    Execute Command  i3c.slave1 LastCommandCode
    Should Be Equal As Numbers  ${ccc1}  0x80
    # slave0 never received the direct CCC (its LastCommandCode is still the -1 sentinel).
    ${ccc0}=                    Execute Command  i3c.slave0 LastCommandCode
    Should Not Be Equal As Numbers  ${ccc0}  0x80

# --------------------------------------------------------------------------------------------------
# In-Band Interrupt
# --------------------------------------------------------------------------------------------------
Should Capture In Band Interrupt
    Create Machine

    ${irq}=                     Execute Command  i3c IRQ IsSet
    Should Be Equal             ${irq.strip()}  False

    Execute Command             i3c.slave1 RequestInBandInterrupt 0xAB

    ${irq}=                     Execute Command  i3c IRQ IsSet
    Should Be Equal             ${irq.strip()}  True
    ${addr}=                    Execute Command  i3c LastInBandInterruptAddress
    Should Be Equal As Numbers  ${addr}  0x09
    ${payload}=                 Execute Command  i3c LastInBandInterruptPayloadHex
    Should Contain              ${payload}  [0xAB]

Should Carry Data In An In Band Interrupt
    Create Machine

    Execute Command             i3c.slave0 RequestInBandInterruptWithData 0x5A "112233"
    ${payload}=                 Execute Command  i3c LastInBandInterruptPayloadHex
    Should Contain              ${payload}  [0x5A, 0x11, 0x22, 0x33]

Should Acknowledge In Band Interrupt
    Create Machine

    Execute Command             i3c.slave1 RequestInBandInterrupt 0xAB
    ${irq}=                     Execute Command  i3c IRQ IsSet
    Should Be Equal             ${irq.strip()}  True

    Execute Command             i3c AcknowledgeInBandInterrupt
    ${irq}=                     Execute Command  i3c IRQ IsSet
    Should Be Equal             ${irq.strip()}  False

# --------------------------------------------------------------------------------------------------
# TCP bridge round-trip
# --------------------------------------------------------------------------------------------------
Should Bridge Raw Data Over TCP
    Create Machine
    Execute Command             emulation CreateI3CTCPBridge sysbus.i3c 0x08 ${BRIDGE_PORT}

    # Queue the bytes the target will "transmit" back on the read side of the exchange.
    Execute Command             i3c.slave0 EnqueueResponseBytesHex "01020304"

    # Send raw bytes over TCP; the bridge writes them to the target and returns the target's response.
    ${response}=                Transfer Over I3C Bridge  ${BRIDGE_PORT}  DEADBEEF
    Should Be Equal             ${response}  01020304

    # The raw bytes sent over TCP reached the target as a private write.
    ${rx}=                      Execute Command  i3c.slave0 LastReceivedHex
    Should Contain              ${rx}  [0xDE, 0xAD, 0xBE, 0xEF]
