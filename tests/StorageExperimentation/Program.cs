﻿using NeoFx.Models;
using NeoFx.Storage.RocksDb;
using RocksDbSharp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace StorageExperimentation
{
    internal static class Program
    {
        private static void Main()
        {
            var cpArchivePath = Path.GetFullPath("./cp1.neo-express-checkpoint");
            if (!File.Exists(cpArchivePath))
            {
                throw new Exception("Can't find checkpoint archive");
            }

            var murmur = Murmur.MurmurHash.Create32();
            var hashArray = murmur.ComputeHash(System.Text.Encoding.UTF8.GetBytes(cpArchivePath));
            var hash = BitConverter.ToUInt32(hashArray);

            Console.WriteLine($"{cpArchivePath} {hash}");

            var cpTempPath = Path.Combine(Path.GetTempPath(), $"NeoFX.StorageExperimentation.{hash}");
            if (Directory.Exists(cpTempPath))
            {
                Directory.Delete(cpTempPath, true);
            }

            System.IO.Compression.ZipFile.ExtractToDirectory(cpArchivePath, cpTempPath);
            Console.WriteLine(cpTempPath);

            using var storage = new RocksDbStore(cpTempPath);
            if (storage.TryGetBlock(0, out var block))
            {
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    var tx = block.Transactions.Span[i];
                    Console.WriteLine(tx.Type);
                }
            }
        }

        public const string BLOCK_FAMILY = "data:block";
        public const string TX_FAMILY = "data:transaction";
        public const string ACCOUNT_FAMILY = "st:account";
        public const string ASSET_FAMILY = "st:asset";
        public const string CONTRACT_FAMILY = "st:contract";
        public const string HEADER_HASH_LIST_FAMILY = "ix:header-hash-list";
        public const string SPENT_COIN_FAMILY = "st:spent-coin";
        public const string STORAGE_FAMILY = "st:storage";
        public const string UNSPENT_COIN_FAMILY = "st:coin";
        public const string VALIDATOR_FAMILY = "st:validator";
        public const string METADATA_FAMILY = "metadata";
        public const string GENERAL_STORAGE_FAMILY = "general-storage";

        public const byte VALIDATORS_COUNT_KEY = 0x90;
        public const byte CURRENT_BLOCK_KEY = 0xc0;
        public const byte CURRENT_HEADER_KEY = 0xc1;

        public static ColumnFamilies ColumnFamilies => new ColumnFamilies {
                { BLOCK_FAMILY, new ColumnFamilyOptions() },
                { TX_FAMILY, new ColumnFamilyOptions() },
                { ACCOUNT_FAMILY, new ColumnFamilyOptions() },
                { UNSPENT_COIN_FAMILY, new ColumnFamilyOptions() },
                { SPENT_COIN_FAMILY, new ColumnFamilyOptions() },
                { VALIDATOR_FAMILY, new ColumnFamilyOptions() },
                { ASSET_FAMILY, new ColumnFamilyOptions() },
                { CONTRACT_FAMILY, new ColumnFamilyOptions() },
                { STORAGE_FAMILY, new ColumnFamilyOptions() },
                { HEADER_HASH_LIST_FAMILY, new ColumnFamilyOptions() },
                { METADATA_FAMILY, new ColumnFamilyOptions() },
                { GENERAL_STORAGE_FAMILY, new ColumnFamilyOptions() }};

        private delegate bool TryRead<T>(ReadOnlyMemory<byte> span, out T key);
        private delegate bool TryWriteKey<TKey>(in TKey key, Span<byte> span);

        private static IEnumerable<(TKey key, TValue value)> Iterate<TKey, TValue>(
            RocksDb db,
            string familyName,
            TryRead<TKey> tryReadKey,
            TryRead<TValue> tryReadValue)
        {
            using var iterator = db.NewIterator(db.GetColumnFamily(familyName));
            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                var keyReadResult = tryReadKey(iterator.Key(), out var key);
                var valueReadResult = tryReadValue(iterator.Value(), out var value);

                Debug.Assert(keyReadResult && iterator.Key().Length == UInt256.Size);
                Debug.Assert(valueReadResult);

                yield return (key, value);
                iterator.Next();
            }
        }

        private static bool TryGet<TKey, TValue>(
            RocksDb db,
            string columnFamily,
            TKey key,
            [MaybeNull] out TValue value,
            int keySize,
            int valueSize,
            TryWriteKey<TKey> tryWriteKey,
            TryRead<TValue> tryReadValue)
        {
            var keyBuffer = ArrayPool<byte>.Shared.Rent(keySize);
            var valueBuffer = ArrayPool<byte>.Shared.Rent(valueSize);
            try
            {
                if (tryWriteKey(key, keyBuffer.AsSpan().Slice(0, keySize)))
                {
                    var count = db.Get(keyBuffer, keySize, valueBuffer, 0, valueSize, db.GetColumnFamily(columnFamily));
                    if (count >= 0)
                    {
                        Debug.Assert(count < valueSize);
                        return tryReadValue(valueBuffer.AsMemory().Slice(0, (int)count), out value);
                    }
                }

#pragma warning disable CS8653 // A default expression introduces a null value for a type parameter.
                value = default;
#pragma warning restore CS8653 // A default expression introduces a null value for a type parameter.
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(keyBuffer);
                ArrayPool<byte>.Shared.Return(valueBuffer);
            }
        }

        private static bool TryReadStateVersion(ref SequenceReader<byte> reader, byte expectedVersion)
        {
            if (reader.TryPeek(out var value) && value == expectedVersion)
            {
                reader.Advance(sizeof(byte));
                return true;
            }

            return false;
        }

        private static bool TryReadUInt256(ReadOnlyMemory<byte> memory, out UInt256 key)
        {
            return UInt256.TryReadBytes(memory.Span, out key);
        }

        private static bool TryWriteUInt256(in UInt256 key, Span<byte> span)
        {
            return key.TryWriteBytes(span);
        }

        private static bool TryReadBlockState(ReadOnlyMemory<byte> memory, out (long systemFee, TrimmedBlock block) value)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(memory));

            if (TryReadStateVersion(ref reader, 0)
                && reader.TryReadInt64LittleEndian(out var systemFee)
                && TrimmedBlock.TryRead(ref reader, out var block))
            {
                Debug.Assert(reader.Remaining == 0);
                value = (systemFee, block);
                return true;
            }

            value = default;
            return false;
        }

        static bool TryReadTransactionState(ReadOnlyMemory<byte> memory, out (uint blockIndex, Transaction tx) value)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(memory));

            if (TryReadStateVersion(ref reader, 0)
                && reader.TryReadUInt32LittleEndian(out var blockIndex)
                && Transaction.TryRead(ref reader, out var tx))
            {
                Debug.Assert(reader.Remaining == 0);
                value = (blockIndex, tx);
                return true;
            }

            value = default;
            return false;
        }

        private static IEnumerable<(UInt256 key, (long systemFee, TrimmedBlock block) blockState)> GetBlocks(RocksDb db)
        {
            return Iterate<UInt256, (long, TrimmedBlock)>
                (db, BLOCK_FAMILY, TryReadUInt256, TryReadBlockState);
        }

        private static bool TryGetBlock(RocksDb db, UInt256 key, out (long systemFee, TrimmedBlock block) value)
        {
            return TryGet(db, BLOCK_FAMILY, key, out value, UInt256.Size, 2048, TryWriteUInt256, TryReadBlockState);
        }

        private static IEnumerable<(UInt256 key, (uint blockIndex, Transaction tx) txState)> GetTransactions(RocksDb db)
        {
            return Iterate<UInt256, (uint, Transaction)>
                (db, TX_FAMILY, TryReadUInt256, TryReadTransactionState);
        }

        private static bool TryGetTransaction(RocksDb db, UInt256 key, out (uint blockIndex, Transaction tx) value)
        {
            return TryGet(db, TX_FAMILY, key, out value, UInt256.Size, 2048, TryWriteUInt256, TryReadTransactionState);
        }

        private static void BlocksAndTransactionsExperiment(string path)
        {
            var options = new DbOptions()
                .SetCreateIfMissing(false)
                .SetCreateMissingColumnFamilies(false);

            Console.WriteLine(path);
            using var db = RocksDb.Open(options, path, ColumnFamilies);

            var blocks = GetBlocks(db).ToDictionary(t => t.key, t => t.blockState);
            var txs = GetTransactions(db).ToDictionary(t => t.key, t => t.txState);

            var blockIndex = blocks.ToDictionary(kvp => kvp.Value.block.Index, t => t.Key);

            for (uint index = 0; index < blockIndex.Count; index++)
            {
                var blockHash = blockIndex[index];
                var (_, block) = blocks[blockHash];
                for (int txIndex = 0; txIndex < block.Hashes.Length; txIndex++)
                {
                    var txHash = block.Hashes.Span[txIndex];
                    var (blockIndex2, tx) = txs[txHash];
                    Debug.Assert(index == blockIndex2);
                }

                if (TryGetBlock(db, blockHash, out var blockState))
                {
                    var hashes = blockState.block.Hashes;
                    for (int z = 0; z < hashes.Length; z++)
                    {
                        if (TryGetTransaction(db, hashes.Span[z], out var txState))
                        {
                            Console.WriteLine(txState.tx.Type);
                        }
                        else
                        {
                            ;
                        }
                    }
                }
                else
                {
                    ;
                }
            }
        }
    }
}
