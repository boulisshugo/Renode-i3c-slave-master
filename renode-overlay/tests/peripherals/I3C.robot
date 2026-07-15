*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/I3C.repl

*** Test Cases ***
Should Assign Dynamic Addresses
    Create Machine

    ${addr0}=                   Execute Command  i3c.slave0 DynamicAddress
    Should Be Equal As Numbers  ${addr0}  0x00

    Execute Command             i3c AssignDynamicAddresses

    ${addr0}=                   Execute Command  i3c.slave0 DynamicAddress
    Should Be Equal As Numbers  ${addr0}  0x08
    ${addr1}=                   Execute Command  i3c.slave1 DynamicAddress
    Should Be Equal As Numbers  ${addr1}  0x09

Should Perform Private Write
    Create Machine

    Execute Command             i3c WritePrivateHex 0x08 "DEADBEEF"

    ${rx}=                      Execute Command  i3c.slave0 LastReceivedHex
    Should Contain              ${rx}  [0xDE, 0xAD, 0xBE, 0xEF]

Should Perform Private Read
    Create Machine

    Execute Command             i3c.slave0 EnqueueResponseBytesHex "0102A0"

    ${rx}=                      Execute Command  i3c ReadPrivateHex 0x08 3
    Should Contain              ${rx}  [0x1, 0x2, 0xA0]

Should Deliver Broadcast And Direct CCC
    Create Machine

    # RSTDAA (0x06) broadcast reaches every target.
    Execute Command             i3c SendBroadcastCommandCode 0x06
    ${ccc0}=                    Execute Command  i3c.slave0 LastCommandCode
    Should Be Equal As Numbers  ${ccc0}  0x06
    ${ccc1}=                    Execute Command  i3c.slave1 LastCommandCode
    Should Be Equal As Numbers  ${ccc1}  0x06

    # A direct CCC (0x80) reaches only the addressed target.
    Execute Command             i3c SendDirectCommandCode 0x80 0x09
    ${ccc1}=                    Execute Command  i3c.slave1 LastCommandCode
    Should Be Equal As Numbers  ${ccc1}  0x80
    # slave0 still holds the previous broadcast code.
    ${ccc0}=                    Execute Command  i3c.slave0 LastCommandCode
    Should Be Equal As Numbers  ${ccc0}  0x06

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

    Execute Command             i3c AcknowledgeInBandInterrupt
    ${irq}=                     Execute Command  i3c IRQ IsSet
    Should Be Equal             ${irq.strip()}  False
