﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace NeoFx.Models
{
    public readonly struct Witness
    {
        public readonly ReadOnlyMemory<byte> InvocationScript;
        public readonly ReadOnlyMemory<byte> VerificationScript;

        public readonly int Size => InvocationScript.Length + VerificationScript.Length;

        public Witness(ReadOnlyMemory<byte> invocationScript, ReadOnlyMemory<byte> verificationScript)
        {
            InvocationScript = invocationScript;
            VerificationScript = verificationScript;
        }
    }
}