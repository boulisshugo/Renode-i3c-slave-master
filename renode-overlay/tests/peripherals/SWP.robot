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

Create Activated Machine
    Create Machine
    ${ok}=                      Execute Command  swp Activate 0
    Should Be Equal             ${ok.strip()}  True

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# Data link layer (ETSI TS 102 613 clause 8): SOF/EOF flags, bit stuffing and the CRC
# --------------------------------------------------------------------------------------------------
Should Compute The Standard Frame CRC
    Create Machine

    # The CRC is X^16 + X^12 + X^5 + 1 with initial value 'FFFF'; its check value over the ASCII
    # string "123456789" is '29B1'.
    ${crc}=                     Execute Command  swp ComputeFrameCrc "313233343536373839"
    Should Contain              ${crc}  0x29B1

    ${reference}=               Swp Crc  313233343536373839
    Should Be Equal             ${reference}  0x29B1

Should Frame A Payload With SOF EOF And CRC
    Create Machine

    # 'C001' is an SHDLC RR acknowledging N(R) = 1. Nothing in it needs stuffing, so the frame is a
    # plain SOF | payload | CRC | EOF.
    ${wire}=                    Execute Command  swp EncodeFrameHex "C001"
    Should Contain              ${wire}  [0x7E, 0xC0, 0x1, 0x1B, 0x7A, 0x7F]

Should Bit Stuff Runs Of Five Ones
    Create Machine

    # Four 'FF' bytes are 32 consecutive ones: every run of five gets a 0 inserted, so the frame is
    # no longer a byte-aligned copy of the payload and the EOF flag can never be imitated.
    ${wire}=                    Execute Command  swp EncodeFrameHex "FFFFFFFF"
    Should Contain              ${wire}  [0x7E, 0xFB, 0xEF, 0xBE, 0xFB, 0xEC, 0x74, 0x3D, 0xFC]

    ${payload}=                 Execute Command  swp DecodeFrameHex "7EFBEFBEFBEC743DFC"
    Should Contain              ${payload}  [0xFF, 0xFF, 0xFF, 0xFF]

Should Round Trip A Framed Payload
    Create Machine

    ${wire}=                    Execute Command  swp EncodeFrameHex "80DEADBEEF"
    Should Contain              ${wire}  [0x7E, 0x80, 0xDE, 0xAD, 0xBE, 0x77, 0xDD, 0xE2, 0xDF, 0xC0]

    ${payload}=                 Execute Command  swp DecodeFrameHex "7E80DEADBE77DDE2DFC0"
    Should Contain              ${payload}  [0x80, 0xDE, 0xAD, 0xBE, 0xEF]

Should Reject A Frame With A Bad CRC
    Create Machine

    # One flipped payload bit in the frame above.
    ${result}=                  Execute Command  swp DecodeFrameHex "7E809EADBE77DDE2DFC0"
    Should Contain              ${result}  CRC mismatch

# --------------------------------------------------------------------------------------------------
# ACT LLC (clause 11): the interface activation sequence
# --------------------------------------------------------------------------------------------------
Should Activate The Interface
    Create Machine

    ${state}=                   Execute Command  swp InterfaceState
    Should Contain              ${state}  Deactivated

    ${ok}=                      Execute Command  swp Activate 0
    Should Be Equal             ${ok.strip()}  True

    ${state}=                   Execute Command  swp InterfaceState
    Should Contain              ${state}  Activated
    ${state}=                   Execute Command  swp.uicc InterfaceState
    Should Contain              ${state}  Activated

Should Select The Power Mode In ACT_POWER_MODE
    Create Machine

    Execute Command             swp PowerMode FullPower
    Execute Command             swp Activate 0

    ${mode}=                    Execute Command  swp.uicc PowerMode
    Should Contain              ${mode}  FullPower

Should Read The Capabilities The UICC Advertises In ACT_SYNC
    Create Activated Machine

    # DummySWPTarget advertises a 4096-byte maximum frame payload by default.
    ${size}=                    Execute Command  swp GetTargetMaxFramePayloadSize 0
    Should Be Equal As Numbers  ${size}  4096

Should Refuse A Payload Larger Than The UICC Advertised
    Create Machine
    Create Log Tester           1
    Execute Command             swp.uicc MaxFramePayloadSize 8
    Execute Command             swp Activate 0

    ${answer}=                  Execute Command  swp SendHex 0 "0102030405060708090A"
    Should Contain              ${answer}  []
    Wait For Log Entry          exceeds the 8-byte maximum

# --------------------------------------------------------------------------------------------------
# SHDLC LLC (clause 10): link establishment and sequenced data transfer
# --------------------------------------------------------------------------------------------------
Should Establish The SHDLC Link
    Create Activated Machine

    ${established}=             Execute Command  swp LinkEstablished
    Should Be Equal             ${established.strip()}  True
    ${established}=             Execute Command  swp.uicc LinkEstablished
    Should Be Equal             ${established.strip()}  True

    ${window}=                  Execute Command  swp GetWindowSize 0
    Should Be Equal As Numbers  ${window}  4

Should Negotiate The Window Size Down To What The UICC Accepts
    Create Machine
    Execute Command             swp.uicc MaxWindowSize 2
    Execute Command             swp Activate 0

    ${window}=                  Execute Command  swp GetWindowSize 0
    Should Be Equal As Numbers  ${window}  2

Should Exchange Data Once The Link Is Up
    Create Activated Machine

    Execute Command             swp.uicc EnqueueResponsePayloadHex "01020304"

    ${answer}=                  Execute Command  swp SendHex 0 "DEADBEEF"
    Should Contain              ${answer}  [0x1, 0x2, 0x3, 0x4]

    ${rx}=                      Execute Command  swp.uicc LastReceivedPayloadHex
    Should Contain              ${rx}  [0xDE, 0xAD, 0xBE, 0xEF]

Should Acknowledge With A Bare RR When The UICC Has Nothing To Say
    Create Activated Machine

    ${answer}=                  Execute Command  swp SendHex 0 "AABB"
    Should Contain              ${answer}  []

    ${rx}=                      Execute Command  swp.uicc LastReceivedPayloadHex
    Should Contain              ${rx}  [0xAA, 0xBB]

Should Keep The Sequence Numbers In Step Across The Modulo 8 Wrap
    Create Activated Machine

    # N(S) and N(R) are modulo 8, so twenty exchanges wrap them twice. A single slip would make the
    # UICC answer with a REJ instead of accepting the frame.
    Repeat Keyword              20 times  Execute Command  swp SendHex 0 "BBCC"

    ${count}=                   Execute Command  swp.uicc ReceivedCount
    Should Be Equal As Numbers  ${count}  20
    ${rejects}=                 Execute Command  swp.uicc RejectsSent
    Should Be Equal As Numbers  ${rejects}  0
    ${errors}=                  Execute Command  swp CrcErrors
    Should Be Equal As Numbers  ${errors}  0
    ${retransmissions}=         Execute Command  swp Retransmissions
    Should Be Equal As Numbers  ${retransmissions}  0

Should Refuse To Send Before The Link Is Established
    Create Machine
    Create Log Tester           1

    ${answer}=                  Execute Command  swp SendHex 0 "AABB"
    Should Contain              ${answer}  []
    Wait For Log Entry          the SHDLC link is not established

# --------------------------------------------------------------------------------------------------
# Point to point: a CLF line addresses exactly one UICC
# --------------------------------------------------------------------------------------------------
Should Isolate Traffic To The Addressed SWP Line
    Create Activated Machine

    Execute Command             swp SendHex 0 "AA"

    ${rx}=                      Execute Command  swp.ese LastReceivedPayloadHex
    Should Contain              ${rx}  []
    ${state}=                   Execute Command  swp.ese InterfaceState
    Should Contain              ${state}  Deactivated

Should Warn On Access To A Missing SWP Line
    Create Machine
    Create Log Tester           1

    Execute Command             swp Activate 7
    Wait For Log Entry          No SWP target registered on line 7

# --------------------------------------------------------------------------------------------------
# The UICC transmitting on its own initiative (SWP is full duplex)
# --------------------------------------------------------------------------------------------------
Should Raise IRQ On An Unsolicited Frame From The UICC
    Create Activated Machine

    ${irq}=                     Execute Command  swp IRQ IsSet
    Should Be Equal             ${irq.strip()}  False

    Execute Command             swp.uicc RequestServiceWithData "112233"

    ${irq}=                     Execute Command  swp IRQ IsSet
    Should Be Equal             ${irq.strip()}  True
    ${line}=                    Execute Command  swp LastReceivedLine
    Should Be Equal As Numbers  ${line}  0
    ${payload}=                 Execute Command  swp LastReceivedPayloadHex
    Should Contain              ${payload}  [0x11, 0x22, 0x33]

Should Acknowledge The Interrupt
    Create Activated Machine

    Execute Command             swp.uicc RequestServiceWithData "44"
    ${irq}=                     Execute Command  swp IRQ IsSet
    Should Be Equal             ${irq.strip()}  True

    Execute Command             swp AcknowledgeInterrupt
    ${irq}=                     Execute Command  swp IRQ IsSet
    Should Be Equal             ${irq.strip()}  False

Should Stay In Sequence After An Unsolicited Frame
    Create Activated Machine

    Execute Command             swp.uicc RequestServiceWithData "5566"
    Execute Command             swp.uicc EnqueueResponsePayloadHex "77"

    ${answer}=                  Execute Command  swp SendHex 0 "88"
    Should Contain              ${answer}  [0x77]
    ${rejects}=                 Execute Command  swp.uicc RejectsSent
    Should Be Equal As Numbers  ${rejects}  0

# --------------------------------------------------------------------------------------------------
# Deactivation
# --------------------------------------------------------------------------------------------------
Should Drop All State On Deactivation
    Create Activated Machine

    Execute Command             swp Deactivate 0

    ${state}=                   Execute Command  swp InterfaceState
    Should Contain              ${state}  Deactivated
    ${state}=                   Execute Command  swp.uicc InterfaceState
    Should Contain              ${state}  Deactivated
    ${established}=             Execute Command  swp LinkEstablished
    Should Be Equal             ${established.strip()}  False

Should Re-Activate After A Deactivation
    Create Activated Machine

    Execute Command             swp Deactivate 0
    ${ok}=                      Execute Command  swp Activate 0
    Should Be Equal             ${ok.strip()}  True

    Execute Command             swp.uicc EnqueueResponsePayloadHex "99"
    ${answer}=                  Execute Command  swp SendHex 0 "11"
    Should Contain              ${answer}  [0x99]

# --------------------------------------------------------------------------------------------------
# TCP bridge round-trip
# --------------------------------------------------------------------------------------------------
Should Bridge Raw Payloads Over TCP
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SWP-consistency.repl
    Execute Command             swp Activate 0
    Execute Command             emulation CreateSWPTCPBridge sysbus.swp 0 ${BRIDGE_PORT}
    # The bridge marshals the exchange into the time domain, so the emulation must be running.
    Start Emulation

    ${response}=                Transfer Over Swp Bridge  ${BRIDGE_PORT}  DEADBEEF
    Should Be Equal             ${response}  deadbeef
