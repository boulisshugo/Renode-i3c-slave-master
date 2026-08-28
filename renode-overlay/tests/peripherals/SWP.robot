*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/SWP-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33669

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SWP.repl

Create Powered Machine
    Create Machine
    Execute Command             swp PowerUp 0

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# Power: the CLF owns it, and it gates the wire. Powering up runs NO activation sequence - the SWP
# models are a transport, so any ACT exchange belongs to the stack under test.
# --------------------------------------------------------------------------------------------------
Should Start Unpowered
    Create Machine

    ${powered}=                 Execute Command  swp Powered
    Should Be Equal             ${powered.strip()}  False
    ${powered}=                 Execute Command  swp.uicc Powered
    Should Be Equal             ${powered.strip()}  False

Should Power The Line Up And Down
    Create Machine

    Execute Command             swp PowerUp 0
    ${powered}=                 Execute Command  swp.uicc Powered
    Should Be Equal             ${powered.strip()}  True

    Execute Command             swp PowerDown 0
    ${powered}=                 Execute Command  swp.uicc Powered
    Should Be Equal             ${powered.strip()}  False

Should Exchange No Bytes While Powering Up
    Create Machine

    Execute Command             swp PowerUp 0

    # No activation sequence, no handshake - powering the line moves no data at all.
    ${sent}=                    Execute Command  swp BytesSent
    Should Be Equal As Numbers  ${sent}  0
    ${received}=                Execute Command  swp BytesReceived
    Should Be Equal As Numbers  ${received}  0
    ${rx}=                      Execute Command  swp.uicc LastReceivedHex
    Should Contain              ${rx}  []

Should Refuse To Transfer On An Unpowered Line
    Create Machine
    Create Log Tester           1

    ${answer}=                  Execute Command  swp TransferHex 0 "AABB"
    Should Contain              ${answer}  []
    Wait For Log Entry          is not powered

Should Warn On Access To A Missing SWP Line
    Create Machine
    Create Log Tester           1

    Execute Command             swp TransferHex 7 "AB"
    Wait For Log Entry          No SWP target registered on line 7

# --------------------------------------------------------------------------------------------------
# Full-duplex byte carriage
# --------------------------------------------------------------------------------------------------
Should Carry Bytes To The Target
    Create Powered Machine

    Execute Command             swp TransferHex 0 "DEADBEEF"

    ${rx}=                      Execute Command  swp.uicc LastReceivedHex
    Should Contain              ${rx}  [0xDE, 0xAD, 0xBE, 0xEF]

Should Carry Bytes Back In The Same Slot
    Create Powered Machine

    Execute Command             swp.uicc EnqueueResponseHex "01020304"

    ${answer}=                  Execute Command  swp TransferHex 0 "AA"
    Should Contain              ${answer}  [0x1, 0x2, 0x3, 0x4]

Should Let The Target Talk On An Empty Slot
    Create Powered Machine

    Execute Command             swp.uicc EnqueueResponseHex "77"

    ${answer}=                  Execute Command  swp ReceiveHex 0
    Should Contain              ${answer}  [0x77]

Should Return Nothing When The Target Has Nothing To Say
    Create Powered Machine

    ${answer}=                  Execute Command  swp TransferHex 0 "AABB"
    Should Contain              ${answer}  []

Should Isolate Traffic To The Addressed SWP Line
    Create Powered Machine

    Execute Command             swp TransferHex 0 "AA"

    ${rx}=                      Execute Command  swp.ese LastReceivedHex
    Should Contain              ${rx}  []
    ${powered}=                 Execute Command  swp.ese Powered
    Should Be Equal             ${powered.strip()}  False

# --------------------------------------------------------------------------------------------------
# Transparency: the transport adds and removes nothing, so bytes a framing layer would have to
# escape pass through untouched.
# --------------------------------------------------------------------------------------------------
Should Carry Flag-Like Bytes Unchanged
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SWP-consistency.repl
    Execute Command             swp PowerUp 0

    # 7E and 7F are the SOF/EOF flags of the framing this transport deliberately does not do, so
    # they must cross the wire untouched.
    ${answer}=                  Execute Command  swp TransferHex 0 "7E7F7E7FFFFFFFFF00"
    Should Contain              ${answer}  [0x7E, 0x7F, 0x7E, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0x0]

# --------------------------------------------------------------------------------------------------
# The target driving S2 unprompted
# --------------------------------------------------------------------------------------------------
Should Raise IRQ On Unsolicited Data
    Create Powered Machine

    ${irq}=                     Execute Command  swp IRQ IsSet
    Should Be Equal             ${irq.strip()}  False

    Execute Command             swp.uicc SendDataHex "112233"

    ${irq}=                     Execute Command  swp IRQ IsSet
    Should Be Equal             ${irq.strip()}  True
    ${line}=                    Execute Command  swp LastReceivedLine
    Should Be Equal As Numbers  ${line}  0
    ${payload}=                 Execute Command  swp LastReceivedHex
    Should Contain              ${payload}  [0x11, 0x22, 0x33]

Should Acknowledge The Interrupt
    Create Powered Machine

    Execute Command             swp.uicc SendDataHex "44"
    ${irq}=                     Execute Command  swp IRQ IsSet
    Should Be Equal             ${irq.strip()}  True

    Execute Command             swp AcknowledgeInterrupt
    ${irq}=                     Execute Command  swp IRQ IsSet
    Should Be Equal             ${irq.strip()}  False

# --------------------------------------------------------------------------------------------------
# Raw byte trace on the target
# --------------------------------------------------------------------------------------------------
Should Trace Both Directions
    Create Powered Machine

    Execute Command             swp.uicc ClearTrace
    Execute Command             swp.uicc EnqueueResponseHex "BB"
    Execute Command             swp TransferHex 0 "AA"
    Execute Command             swp.uicc SendDataHex "CC"

    ${trace}=                   Execute Command  swp.uicc TraceHex
    Should Contain              ${trace}  in   AA
    Should Contain              ${trace}  out  BB
    Should Contain              ${trace}  out  CC

Should Let The Trace Be Turned Off
    Create Machine
    Execute Command             swp.uicc TraceDepth 0
    Execute Command             swp PowerUp 0
    Execute Command             swp TransferHex 0 "AA"

    ${trace}=                   Execute Command  swp.uicc TraceHex
    Should Contain              ${trace}  nothing traced
    ${last}=                    Execute Command  swp.uicc LastReceivedHex
    Should Contain              ${last}  [0xAA]

# --------------------------------------------------------------------------------------------------
# TCP bridge round-trip - a transparent pipe, no framing added anywhere
# --------------------------------------------------------------------------------------------------
Should Bridge Raw Bytes Over TCP
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SWP-consistency.repl
    Execute Command             swp PowerUp 0
    Execute Command             emulation CreateSWPTCPBridge sysbus.swp 0 ${BRIDGE_PORT}
    # The bridge marshals the transfer into the time domain, so the emulation must be running.
    Start Emulation

    ${response}=                Transfer Over Swp Bridge  ${BRIDGE_PORT}  DEADBEEF
    Should Be Equal             ${response}  deadbeef
