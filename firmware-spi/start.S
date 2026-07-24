/*
 * Minimal RISC-V bare-metal startup: set the stack pointer, zero .bss, call main.
 */
    .section .init
    .globl _start
    .type _start, @function
_start:
    la      sp, _stack_top

    /* zero .bss */
    la      t0, _bss_start
    la      t1, _bss_end
1:
    bgeu    t0, t1, 2f
    sw      zero, 0(t0)
    addi    t0, t0, 4
    j       1b
2:
    call    main

hang:
    j       hang
