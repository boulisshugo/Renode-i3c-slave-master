*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         Process

*** Variables ***
${BRIDGE_PORT}                  33671
${FIRMWARE}                     @tests/peripherals/spi-firmware.elf
# Absolute classpath of the compiled Java client (java-spi/out). Set I3C_SPI_JAVA_CP to enable this suite.
${JAVA_CP}                      %{I3C_SPI_JAVA_CP=}

*** Keywords ***
Create Firmware Machine With Bridge
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SPI-firmware.repl
    Execute Command             sysbus LoadELF ${FIRMWARE}
    Execute Command             emulation CreateSPITCPBridge sysbus.spi 0 ${BRIDGE_PORT} true
    Create Terminal Tester      sysbus.uart
    Start Emulation
    Wait For Line On Uart       spi-firmware: ready

*** Test Cases ***
Should Drive The Firmware Slave From The Java Bridge
    Skip If                     '${JAVA_CP}' == ''    I3C_SPI_JAVA_CP is not set - build the Java client (java-spi/build.sh) and point I3C_SPI_JAVA_CP at java-spi/out
    Create Firmware Machine With Bridge

    ${result}=                  Run Process  java  -cp  ${JAVA_CP}  spi.Main  127.0.0.1  ${BRIDGE_PORT}  300  16  5000
    ...                         timeout=120  stderr=STDOUT
    Log                         ${result.stdout}
    Should Contain              ${result.stdout}  fail=0
    Should Contain              ${result.stdout}  reliability=100.00%
