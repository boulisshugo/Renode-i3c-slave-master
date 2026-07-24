*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/SPI-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33668

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SPI-consistency.repl

Create Machine With Bridge
    Create Machine
    Execute Command             emulation CreateSPITCPBridge sysbus.spi 0 ${BRIDGE_PORT}

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# Direct-API integrity: the MOSI bytes of a transfer reach the target intact.
# --------------------------------------------------------------------------------------------------
Should Preserve The MOSI Bytes Of A Transfer
    Create Machine

    ${data}=                    Random Hex  64
    Execute Command             spi TransferHex 1 "${data}"

    ${pretty}=                  Execute Command  spi.dummy LastReceivedHex
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ${data}

# --------------------------------------------------------------------------------------------------
# Direct-API integrity: the queued MISO bytes are shifted back intact.
# --------------------------------------------------------------------------------------------------
Should Preserve The MISO Bytes Of A Transfer
    Create Machine

    ${data}=                    Random Hex  64
    Execute Command             spi.dummy EnqueueResponseBytesHex "${data}"

    ${clock}=                   Random Hex  64
    ${pretty}=                  Execute Command  spi TransferHex 1 "${clock}"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ${data}

# --------------------------------------------------------------------------------------------------
# Bridge integrity: a large payload sent at once echoes back byte-for-byte.
# --------------------------------------------------------------------------------------------------
Should Echo A Large Payload At Once Over The Bridge
    Create Machine With Bridge

    ${data}=                    Random Hex  2048
    ${response}=                Transfer Over Spi Bridge  ${BRIDGE_PORT}  ${data}
    Should Be Equal             ${response}  ${data}

# --------------------------------------------------------------------------------------------------
# Bridge consistency: many sequential exchanges on one connection all match.
# --------------------------------------------------------------------------------------------------
Should Stay Consistent Over Many Sequential Exchanges
    Create Machine With Bridge

    ${matched}=                 Bridge Sequential Echo  ${BRIDGE_PORT}  256  32
    Should Be Equal As Integers  ${matched}  256

Should Stay Consistent For Larger Sequential Messages
    Create Machine With Bridge

    ${matched}=                 Bridge Sequential Echo  ${BRIDGE_PORT}  64  256
    Should Be Equal As Integers  ${matched}  64
