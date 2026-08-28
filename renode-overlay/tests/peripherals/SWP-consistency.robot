*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/SWP-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33670

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SWP-consistency.repl
    Execute Command             swp PowerUp 0

Create Machine With Bridge
    Create Machine
    Execute Command             emulation CreateSWPTCPBridge sysbus.swp 0 ${BRIDGE_PORT}
    # The bridge marshals every transfer into the time domain, so the emulation must be running.
    Start Emulation

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# The transport is transparent: whatever goes in comes out, byte for byte, with nothing added.
# --------------------------------------------------------------------------------------------------
Should Preserve A Block Across One Transfer
    Create Machine

    ${data}=                    Random Hex  64
    ${pretty}=                  Execute Command  swp TransferHex 0 "${data}"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ${data}

Should Preserve Bytes That Would Need Escaping In A Framed Link
    Create Machine

    ${pretty}=                  Execute Command  swp TransferHex 0 "7E7F7E7FFFFFFFFF00FF7E7F"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  7e7f7e7fffffffff00ff7e7f

Should Preserve An All Ones Block
    Create Machine

    ${pretty}=                  Execute Command  swp TransferHex 0 "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ffffffffffffffffffffffffffffffff

Should Preserve A Large Block
    Create Machine

    ${data}=                    Random Hex  4096
    ${pretty}=                  Execute Command  swp TransferHex 0 "${data}"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ${data}

# --------------------------------------------------------------------------------------------------
# Bridge integrity
# --------------------------------------------------------------------------------------------------
Should Echo A Large Block At Once Over The Bridge
    Create Machine With Bridge

    ${data}=                    Random Hex  2048
    ${response}=                Transfer Over Swp Bridge  ${BRIDGE_PORT}  ${data}
    Should Be Equal             ${response}  ${data}

Should Stay Consistent Over Many Sequential Exchanges
    Create Machine With Bridge

    ${matched}=                 Bridge Sequential Echo  ${BRIDGE_PORT}  256  32
    Should Be Equal As Integers  ${matched}  256

Should Stay Consistent For Larger Sequential Messages
    Create Machine With Bridge

    ${matched}=                 Bridge Sequential Echo  ${BRIDGE_PORT}  64  256
    Should Be Equal As Integers  ${matched}  64
