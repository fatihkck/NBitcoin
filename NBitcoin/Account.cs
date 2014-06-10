﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace NBitcoin
{
	public enum AccountEntryReason
	{
		ChainBlockChanged,
		Income,
		Outcome
	}
	public class AccountEntry : IBitcoinSerializable
	{
		public AccountEntry()
		{

		}
		public AccountEntry(AccountEntryReason reason, uint256 block, Spendable spendable, Money balanceChange)
		{
			_Block = block;
			_Spendable = spendable;
			_BalanceChange = balanceChange;
			_Reason = (byte)reason;
		}

		private uint256 _Block;
		public uint256 Block
		{
			get
			{
				return _Block;
			}
		}

		private byte _Reason;
		public AccountEntryReason Reason
		{
			get
			{
				return (AccountEntryReason)_Reason;
			}
		}


		private Spendable _Spendable;
		public Spendable Spendable
		{
			get
			{
				return _Spendable;
			}
		}

		private Money _BalanceChange = Money.Zero;
		public Money BalanceChange
		{
			get
			{
				return _BalanceChange;
			}
		}

		public AccountEntry Neutralize()
		{
			return new AccountEntry(AccountEntryReason.ChainBlockChanged, Block, Spendable, -BalanceChange);
		}

		#region IBitcoinSerializable Members

		public void ReadWrite(BitcoinStream stream)
		{
			var b = _Block == null ? new uint256(0).ToBytes() : _Block.ToBytes();
			stream.ReadWrite(ref b);
			if(!stream.Serializing)
				_Block = new uint256(b) == 0 ? null : new uint256(b);
			stream.ReadWrite(ref _Reason);
			stream.ReadWrite(ref _Spendable);

			var change = BalanceChange.Satoshi.ToByteArray();
			stream.ReadWriteAsVarString(ref change);
			if(!stream.Serializing)
				_BalanceChange = new Money(new BigInteger(change));
		}

		#endregion
	}
	public class Spendable : IBitcoinSerializable
	{
		public Spendable()
		{

		}
		public Spendable(OutPoint output, TxOut txout)
		{
			if(output == null)
				throw new ArgumentNullException("output");
			if(txout == null)
				throw new ArgumentNullException("txout");
			_Out = txout;
			_OutPoint = output;
		}

		private OutPoint _OutPoint;
		public OutPoint OutPoint
		{
			get
			{
				return _OutPoint;
			}
		}
		private TxOut _Out;
		public TxOut TxOut
		{
			get
			{
				return _Out;
			}
		}

		#region IBitcoinSerializable Members

		public void ReadWrite(BitcoinStream stream)
		{
			stream.ReadWrite(ref _OutPoint);
			if(stream.Serializing)
			{
				TxOutCompressor compressor = new TxOutCompressor(_Out);
				stream.ReadWrite(ref compressor);
			}
			else
			{
				TxOutCompressor compressor = new TxOutCompressor();
				stream.ReadWrite(ref compressor);
				_Out = compressor.TxOut;
			}
		}

		#endregion
	}
	public class Account
	{
		public Account()
			: this(null as ObjectStream<AccountEntry>)
		{

		}
		public Account(ObjectStream<AccountEntry> entries)
		{
			if(entries == null)
				entries = new StreamObjectStream<AccountEntry>();
			entries.Rewind();
			_Entries = entries;
			Process();
		}

		public Account(Account copied)
			: this(copied, null)
		{
		}
		public Account(Account copied, ObjectStream<AccountEntry> entries)
		{
			if(entries == null)
				entries = new StreamObjectStream<AccountEntry>();
			_Entries = entries;
			copied.Entries.Rewind();
			entries.Rewind();
			foreach(var entry in copied.Entries.Enumerate())
			{
				if(_NextToProcess < copied._NextToProcess)
				{
					PushAccountEntry(entry);
				}
				else
					entries.WriteNext(entry);
			}
		}


		private readonly ObjectStream<AccountEntry> _Entries;
		public ObjectStream<AccountEntry> Entries
		{
			get
			{
				return _Entries;
			}
		}

		private int _NextToProcess;

		internal AccountEntry PushAccountEntry(uint256 block, Spendable spendable, Money balanceChange)
		{
			return PushAccountEntry(new AccountEntry(balanceChange < 0 ? AccountEntryReason.Outcome : AccountEntryReason.Income, block, spendable, balanceChange));
		}
		public AccountEntry PushAccountEntry(AccountEntry entry)
		{
			if(Process(entry) != null)
			{
				_Entries.WriteNext(entry);
				_NextToProcess++;
			}
			return entry;
		}

		internal void PushEntry(WalletEntry entry)
		{
			if(entry.Type == WalletEntryType.Income)
			{
				var spendable = entry.GetSpendable();
				PushAccountEntry(entry.Block, spendable, spendable.TxOut.Value);
			}
			else if(entry.Type == WalletEntryType.Outcome)
			{
				if(_Unspent.ContainsKey(entry.OutPoint))
				{
					var spendable = _Unspent[entry.OutPoint];
					PushAccountEntry(entry.Block, spendable, -spendable.TxOut.Value);
				}
			}
		}




		Money _Balance = Money.Zero;
		public Money Balance
		{
			get
			{
				return _Balance;
			}
		}





		Dictionary<OutPoint, Spendable> _Unspent = new Dictionary<OutPoint, Spendable>();
		public IEnumerable<Spendable> Unspent
		{
			get
			{
				return _Unspent.Values;
			}
		}

		public Spendable[] GetEntriesToCover(Money money)
		{
			if(Balance < money)
				return null;
			var result = new List<Spendable>();
			Money current = Money.Zero;
			var unspent = Unspent.OrderBy(o => o.TxOut.Value.Satoshi).ToArray();
			int i = 0;
			while(current < money)
			{
				result.Add(unspent[i]);
				current += unspent[i].TxOut.Value;
				i++;
			}
			return result.ToArray();
		}


		//Do not get all entries, but only the one you can generate with spent/unspent.
		public AccountEntry[] GetInChain(Chain chain, bool value)
		{
			Dictionary<OutPoint, AccountEntry> entries = new Dictionary<OutPoint, AccountEntry>();
			foreach(var entry in AccountEntries)
			{
				if(entry.Block == null)
					continue;

				if(chain.Contains(entry.Block, false) == value)
				{
					if(entry.Reason == AccountEntryReason.Income && _Unspent.ContainsKey(entry.Spendable.OutPoint))
						entries.AddOrReplace(entry.Spendable.OutPoint, entry);
					if(entry.Reason == AccountEntryReason.ChainBlockChanged && !_Unspent.ContainsKey(entry.Spendable.OutPoint))
						entries.AddOrReplace(entry.Spendable.OutPoint, entry);
				}
			}
			return entries.Values.ToArray();
		}

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();

			foreach(var entry in AccountEntries)
			{
				builder.AppendLine(entry.BalanceChange.ToString(true));
			}

			return builder.ToString();
		}


		public void Process()
		{
			while(true)
			{
				var entry = Entries.ReadNext();
				if(entry == null)
					break;
				if(Process(entry) != null)
					_NextToProcess++;
			}
		}

		internal AccountEntry Process(AccountEntry entry)
		{
			try
			{
				if(entry.Reason != AccountEntryReason.ChainBlockChanged)
				{
					if(entry.BalanceChange < Money.Zero)
					{
						if(!_Unspent.Remove(entry.Spendable.OutPoint))
							return null;
					}
					if(entry.BalanceChange > Money.Zero)
					{
						if(!_Unspent.TryAdd(entry.Spendable.OutPoint, entry.Spendable))
							return null;
					}
					if(entry.BalanceChange == Money.Zero)
						return null;
				}
				else
				{
					if(entry.BalanceChange < Money.Zero)
					{
						if(!_Unspent.Remove(entry.Spendable.OutPoint))
							return null;
					}
					if(entry.BalanceChange > Money.Zero)
					{
						if(!_Unspent.TryAdd(entry.Spendable.OutPoint, entry.Spendable))
							return null;
					}
				}
				_Balance += entry.BalanceChange;
				return entry;
			}
			finally
			{
#if DEBUG
				if(_Balance != Unspent.Select(o => o.TxOut.Value).Sum())
					throw new NotSupportedException("Something is going wrong");
#endif
			}
		}


		public IEnumerable<AccountEntry> AccountEntries
		{
			get
			{
				List<AccountEntry> entries = new List<AccountEntry>();
				_Entries.Rewind();
				while(_Entries.Position < _NextToProcess)
				{
					if(_Entries.EOF)
						throw new InvalidOperationException("The entries stream is shorter than during initialization");
					entries.Add(_Entries.ReadNext());
				}
				return entries;
			}
		}

		public Account Clone()
		{
			return Clone(null);
		}
		public Account Clone(ObjectStream<AccountEntry> entries)
		{
			return new Account(this, entries);
		}
	}
}
