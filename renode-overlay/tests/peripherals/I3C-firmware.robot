*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/I3C-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33569
${FIRMWARE}                     @tests/peripherals/i3c-firmware.elf

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/I3C-firmware.repl
    Execute Command             sysbus LoadELF ${FIRMWARE}
    # forward-on-interrupt mode: the firmware's response arrives via an In-Band Interrupt.
    Execute Command             emulation CreateI3CTCPBridge sysbus.i3c 0x08 ${BRIDGE_PORT} true

*** Test Cases ***
Should Boot The Firmware
    Create Machine
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       i3c-firmware: ready

Should Round-Trip A Message Through The Firmware
    Create Machine
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       i3c-firmware: ready

    # Java/TCP client -> bridge -> master -> InventedI3CTarget -> firmware -> (echo) -> IBI -> back.
    ${response}=                Transfer Over I3C Bridge  ${BRIDGE_PORT}  48656C6C6F  timeout=10
    Should Be Equal             ${response}  48656c6c6f
    Wait For Line On Uart       i3c-firmware: echoed a message

Should Stay Reliable Over Many Firmware Round-Trips
    Create Machine
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       i3c-firmware: ready

    ${matched}=                 Bridge Sequential Echo  ${BRIDGE_PORT}  200  16  timeout=20
    Should Be Equal As Integers  ${matched}  200
