*** Settings ***
Suite Setup                     Setup
Suite Teardown                  Teardown
Test Setup                      Reset Emulation
Test Teardown                   Test Teardown
Resource                        ${RENODEKEYWORDS}
Library                         ${CURDIR}/SWP-helpers.py

*** Variables ***
${BRIDGE_PORT}                  33666

*** Keywords ***
Create Machine
    Execute Command             using sysbus
    Execute Command             mach create
    Execute Command             machine LoadPlatformDescription @tests/peripherals/SWP-consistency.repl
    ${ok}=                      Execute Command  swp Activate 0
    Should Be Equal             ${ok.strip()}  True

Create Machine With Bridge
    Create Machine
    Execute Command             emulation CreateSWPTCPBridge sysbus.swp 0 ${BRIDGE_PORT}
    # The bridge marshals every exchange into the time domain, so the emulation must be running.
    Start Emulation

*** Test Cases ***
# --------------------------------------------------------------------------------------------------
# Direct-API integrity: a payload survives framing, bit stuffing, the CRC and SHDLC intact.
# --------------------------------------------------------------------------------------------------
Should Preserve A Payload Across One Exchange
    Create Machine

    ${data}=                    Random Hex  64
    ${pretty}=                  Execute Command  swp SendHex 0 "${data}"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ${data}

# --------------------------------------------------------------------------------------------------
# Payloads whose bit patterns stress the stuffing and the flag detection.
# --------------------------------------------------------------------------------------------------
Should Preserve Payloads That Imitate The Frame Flags
    Create Machine

    ${pretty}=                  Execute Command  swp SendHex 0 "7E7F7E7FFFFFFFFF00FF7E7F"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  7e7f7e7fffffffff00ff7e7f

Should Preserve An All Ones Payload
    Create Machine

    ${pretty}=                  Execute Command  swp SendHex 0 "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ffffffffffffffffffffffffffffffff

# --------------------------------------------------------------------------------------------------
# A large payload in one frame.
# --------------------------------------------------------------------------------------------------
Should Preserve A Large Payload
    Create Machine

    ${data}=                    Random Hex  1024
    ${pretty}=                  Execute Command  swp SendHex 0 "${data}"
    ${got}=                     Normalize Pretty Hex  ${pretty}
    Should Be Equal             ${got}  ${data}

# --------------------------------------------------------------------------------------------------
# Bridge integrity: a large payload sent at once echoes back byte-for-byte.
# --------------------------------------------------------------------------------------------------
Should Echo A Large Payload At Once Over The Bridge
    Create Machine With Bridge

    ${data}=                    Random Hex  1024
    ${response}=                Transfer Over Swp Bridge  ${BRIDGE_PORT}  ${data}
    Should Be Equal             ${response}  ${data}

# --------------------------------------------------------------------------------------------------
# Bridge consistency: many sequential exchanges on one connection all match, and the SHDLC sequence
# numbers stay in step throughout (no REJ, no retransmission, no CRC error).
# --------------------------------------------------------------------------------------------------
Should Stay Consistent Over Many Sequential Exchanges
    Create Machine With Bridge

    ${matched}=                 Bridge Sequential Echo  ${BRIDGE_PORT}  256  32
    Should Be Equal As Integers  ${matched}  256

    ${errors}=                  Execute Command  swp CrcErrors
    Should Be Equal As Numbers  ${errors}  0
    ${retransmissions}=         Execute Command  swp Retransmissions
    Should Be Equal As Numbers  ${retransmissions}  0

Should Stay Consistent For Larger Sequential Messages
    Create Machine With Bridge

    ${matched}=                 Bridge Sequential Echo  ${BRIDGE_PORT}  64  256
    Should Be Equal As Integers  ${matched}  64
