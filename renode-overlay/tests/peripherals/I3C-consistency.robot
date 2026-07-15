*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/I3C-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33568

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/I3C-consistency.repl

Create Machine With Bridge
    Create Machine
    Execute Command             emulation CreateI3CTCPBridge sysbus.i3c 0x08 ${BRIDGE_PORT}

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# Direct-API integrity: a large private write is delivered to the target intact.
# --------------------------------------------------------------------------------------------------
Should Preserve A Large Private Write
    Create Machine

    ${data}=                    Random Hex  64
    Execute Command             i3c WritePrivateHex 0x09 "${data}"

    ${pretty}=                  Execute Command  i3c.dummy LastReceivedHex
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ${data}

# --------------------------------------------------------------------------------------------------
# Direct-API integrity: a large private read returns the queued bytes intact.
# --------------------------------------------------------------------------------------------------
Should Preserve A Large Private Read
    Create Machine

    ${data}=                    Random Hex  64
    Execute Command             i3c.dummy EnqueueResponseBytesHex "${data}"

    ${pretty}=                  Execute Command  i3c ReadPrivateHex 0x09 64
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ${data}

# --------------------------------------------------------------------------------------------------
# Bridge integrity: a large payload sent at once echoes back byte-for-byte.
# --------------------------------------------------------------------------------------------------
Should Echo A Large Payload At Once Over The Bridge
    Create Machine With Bridge

    ${data}=                    Random Hex  2048
    ${response}=                Transfer Over I3C Bridge  ${BRIDGE_PORT}  ${data}
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
