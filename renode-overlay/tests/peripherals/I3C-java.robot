*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         Process

*** Variables ***
${BRIDGE_PORT}                  33571
${FIRMWARE}                     @tests/peripherals/i3c-firmware.elf
# Absolute classpath of the compiled Java client (java/out). Set I3C_JAVA_CP to enable this suite.
${JAVA_CP}                      %{I3C_JAVA_CP=}

*** Keywords ***
Create Firmware Machine With Bridge
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/I3C-firmware.repl
    Execute Command             sysbus LoadELF ${FIRMWARE}
    Execute Command             emulation CreateI3CTCPBridge sysbus.i3c 0x08 ${BRIDGE_PORT} true
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       i3c-firmware: ready

*** Test Cases ***
Should Drive The Firmware Slave From The Java Bridge
    Skip If                     '${JAVA_CP}' == ''    I3C_JAVA_CP is not set - build the Java client (java/build.sh) and point I3C_JAVA_CP at java/out
    Create Firmware Machine With Bridge

    ${result}=                  Run Process  java  -cp  ${JAVA_CP}  i3c.Main  127.0.0.1  ${BRIDGE_PORT}  300  16  5000
    ...                         timeout=120  stderr=STDOUT
    Log                         ${result.stdout}
    Should Contain              ${result.stdout}  fail=0
    Should Contain              ${result.stdout}  reliability=100.00%
