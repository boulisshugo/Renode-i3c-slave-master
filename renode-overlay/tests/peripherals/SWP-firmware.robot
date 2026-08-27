*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/SWP-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33670
${FIRMWARE}                     @tests/peripherals/swp-firmware.elf

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SWP-firmware.repl
    Execute Command             sysbus LoadELF ${FIRMWARE}

Create Activated Machine
    Create Machine
    Create Terminal Tester      sysbus.uart
    # Drive S1 before the CPU runs: the activation event is latched, and the firmware picks it up on
    # its first pass. Nothing is answered until it does.
    Execute Command             swp Activate 0
    Start Emulation
    Wait For Line On Uart       swp-firmware: SHDLC link established

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# The peripheral answers nothing by itself - the firmware owns ACT and SHDLC
# --------------------------------------------------------------------------------------------------
Should Boot The Firmware
    Create Machine
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       swp-firmware: ready

Should Not Answer Activation Without The Firmware
    Create Machine

    # S1 goes up but the CPU never runs, so no ACT_SYNC is ever built. A model that invented one
    # would report an activated link here.
    ${ok}=                      Execute Command  swp Activate 0
    Should Be Equal             ${ok.strip()}  False
    ${established}=             Execute Command  swp LinkEstablished
    Should Be Equal             ${established.strip()}  False
    ${pending}=                 Execute Command  swp IsActivationPending 0
    Should Be Equal             ${pending.strip()}  True

    # The hardware did its half: it latched the event and interrupted the CPU, and sent nothing.
    ${irq}=                     Execute Command  swp.uicc IRQ IsSet
    Should Be Equal             ${irq.strip()}  True
    ${sent}=                    Execute Command  swp.uicc FramesSent
    Should Be Equal As Numbers  ${sent}  0
    ${state}=                   Execute Command  swp.uicc LlcState
    Should Contain              ${state}  Closed

Should Run The Activation Sequence From Firmware
    Create Machine
    Create Terminal Tester      sysbus.uart
    Execute Command             swp Activate 0
    Start Emulation

    # Every one of these lines is printed by firmware-swp/main.c as it builds the frame.
    Wait For Line On Uart       swp-firmware: ACT_SYNC sent
    Wait For Line On Uart       swp-firmware: ACT_READY sent (full power)
    Wait For Line On Uart       swp-firmware: SHDLC link established

    ${established}=             Execute Command  swp LinkEstablished
    Should Be Equal             ${established.strip()}  True
    ${state}=                   Execute Command  swp.uicc LlcState
    Should Contain              ${state}  Established

Should Read The Capabilities The Firmware Advertises In ACT_SYNC
    Create Activated Machine

    # 256 bytes is what firmware-swp/main.c puts in its ACT_INFORMATION - not a model default.
    ${size}=                    Execute Command  swp GetTargetMaxFramePayloadSize 0
    Should Be Equal As Numbers  ${size}  256

    ${window}=                  Execute Command  swp GetWindowSize 0
    Should Be Equal As Numbers  ${window}  4

Should Let A Bench Place S1 And The Activation Event Separately
    Create Machine
    Create Terminal Tester      sysbus.uart

    # A bench that models the power-up order (VPS, then S1, then the ACT event a moment later) turns
    # the automatic coupling off and places each edge itself.
    Execute Command             swp.uicc AutoActivationEvent false
    Execute Command             swp.uicc SetS1 true
    Start Emulation
    Wait For Line On Uart       swp-firmware: ready

    # S1 is up, but no event has been raised, so the firmware has nothing to react to yet.
    ${state}=                   Execute Command  swp.uicc LlcState
    Should Contain              ${state}  Closed
    ${sent}=                    Execute Command  swp.uicc FramesSent
    Should Be Equal As Numbers  ${sent}  0

    Execute Command             swp.uicc TriggerActEvent
    Wait For Line On Uart       swp-firmware: ACT_SYNC sent
    Wait For Line On Uart       swp-firmware: SHDLC link established
    ${established}=             Execute Command  swp LinkEstablished
    Should Be Equal             ${established.strip()}  True

# --------------------------------------------------------------------------------------------------
# Data transfer: the answer is built by the CPU, so it cannot ride the frame that asked for it
# --------------------------------------------------------------------------------------------------
Should Round-Trip A Payload Through The Firmware
    Create Activated Machine
    Execute Command             emulation CreateSWPTCPBridge sysbus.swp 0 ${BRIDGE_PORT} true

    # The demo application reverses the request, so the answer proves the firmware really saw it.
    ${response}=                Transfer Over Swp Bridge  ${BRIDGE_PORT}  0102030405  timeout=10
    Should Be Equal             ${response}  0504030201

Should Stay Reliable Over Many Firmware Round-Trips
    Create Activated Machine
    Execute Command             emulation CreateSWPTCPBridge sysbus.swp 0 ${BRIDGE_PORT} true

    ${matched}=                 Bridge Sequential Reverse  ${BRIDGE_PORT}  100  16  timeout=20
    Should Be Equal As Integers  ${matched}  100

    ${errors}=                  Execute Command  swp CrcErrors
    Should Be Equal As Numbers  ${errors}  0
    ${retransmissions}=         Execute Command  swp Retransmissions
    Should Be Equal As Numbers  ${retransmissions}  0

# --------------------------------------------------------------------------------------------------
# Deactivation reaches the firmware
# --------------------------------------------------------------------------------------------------
Should Tell The Firmware About Deactivation
    Create Activated Machine

    Execute Command             swp Deactivate 0
    Wait For Line On Uart       swp-firmware: interface deactivated

    ${state}=                   Execute Command  swp.uicc InterfaceState
    Should Contain              ${state}  Deactivated
    ${established}=             Execute Command  swp LinkEstablished
    Should Be Equal             ${established.strip()}  False

Should Re-Activate After A Deactivation
    Create Activated Machine

    Execute Command             swp Deactivate 0
    Wait For Line On Uart       swp-firmware: interface deactivated

    Execute Command             swp Activate 0
    Wait For Line On Uart       swp-firmware: SHDLC link established
    ${established}=             Execute Command  swp LinkEstablished
    Should Be Equal             ${established.strip()}  True
