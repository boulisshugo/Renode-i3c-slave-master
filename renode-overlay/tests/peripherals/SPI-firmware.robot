*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/SPI-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33669
${FIRMWARE}                     @tests/peripherals/spi-firmware.elf

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SPI-firmware.repl
    Execute Command             sysbus LoadELF ${FIRMWARE}
    # poll-for-response mode: the master polls the slave for the firmware's length-prefixed response.
    Execute Command             emulation CreateSPITCPBridge sysbus.spi 0 ${BRIDGE_PORT} false true

*** Test Cases ***
Should Boot The Firmware
    Create Machine
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       spi-firmware: ready

Should Round-Trip A Message Through The Firmware
    Create Machine
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       spi-firmware: ready

    # Client -> bridge -> controller -> InventedSPITarget -> firmware -> (echo) -> interrupt -> back.
    ${response}=                Transfer Over Spi Bridge  ${BRIDGE_PORT}  48656C6C6F  timeout=10
    Should Be Equal             ${response}  48656c6c6f
    Wait For Line On Uart       spi-firmware: echoed a message

Should Stay Reliable Over Many Firmware Round-Trips
    Create Machine
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       spi-firmware: ready

    ${matched}=                 Bridge Sequential Echo  ${BRIDGE_PORT}  200  16  timeout=20
    Should Be Equal As Integers  ${matched}  200
