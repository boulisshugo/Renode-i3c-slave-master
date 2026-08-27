*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         Process

*** Variables ***
${BRIDGE_PORT}                  33675
${FIRMWARE}                     @tests/peripherals/swp-firmware.elf
# Absolute classpath of the compiled Java client (java-swp/out). Set SWP_JAVA_CP to enable this suite.
${JAVA_CP}                      %{SWP_JAVA_CP=}

*** Keywords ***
Create Firmware Machine With Lpdu Bridge
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SWP-firmware.repl
    Execute Command             sysbus LoadELF ${FIRMWARE}
    # The LPDU bridge hands the CLF's ACT and SHDLC layers to the TCP client and sets ProtocolOwner
    # to External. PowerUp drives S1 without running any protocol.
    Execute Command             emulation CreateSWPLpduBridge sysbus.swp 0 ${BRIDGE_PORT}
    Execute Command             swp PowerUp 0
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       swp-firmware: ready

*** Test Cases ***
Should Leave Both Protocol Layers Outside The Models
    Create Firmware Machine With Lpdu Bridge

    # Nothing but the wire is modelled here: the CLF's protocol went to the TCP client, the target's
    # to the firmware. With no client connected, the ACT sequence cannot advance past ACT_SYNC.
    ${owner}=                   Execute Command  swp ProtocolOwner
    Should Contain              ${owner}  External
    ${established}=             Execute Command  swp IsLinkEstablished 0
    Should Be Equal             ${established.strip()}  False

Should Run The Whole Activation From The Java Client
    Skip If                     '${JAVA_CP}' == ''    SWP_JAVA_CP is not set - build the Java client (java-swp/build.sh) and point SWP_JAVA_CP at java-swp/out
    Create Firmware Machine With Lpdu Bridge

    # The firmware's ACT_SYNC goes out before the client connects, so the client recovers it with the
    # frame-resend bit and then drives ACT_POWER_MODE, RSET and every sequence number itself.
    ${result}=                  Run Process  java  -cp  ${JAVA_CP}  swp.Main  127.0.0.1  ${BRIDGE_PORT}  200  16  5000  -reverse
    ...                         timeout=180  stderr=STDOUT
    Log                         ${result.stdout}
    Should Contain              ${result.stdout}  Activated in
    Should Contain              ${result.stdout}  fail=0
    Should Contain              ${result.stdout}  reliability=100.00%

    # The link the client established is the one the firmware sees.
    ${state}=                   Execute Command  swp.uicc LlcState
    Should Contain              ${state}  Established
    ${errors}=                  Execute Command  swp CrcErrors
    Should Be Equal As Numbers  ${errors}  0
