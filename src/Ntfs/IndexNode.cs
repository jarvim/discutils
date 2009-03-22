﻿//
// Copyright (c) 2008-2009, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.IO;

namespace DiscUtils.Ntfs
{
    internal delegate void IndexNodeSaveFn();

    internal class IndexNode
    {
        private IndexNodeSaveFn _store;
        private int _storageOverhead;
        private long _totalSpaceAvailable;

        private IndexHeader _header;

        private Index _index;
        private IndexNode _parent;

        private List<IndexEntry> _entries;


        public IndexNode(IndexNodeSaveFn store, int storeOverhead, Index index, IndexNode parent, uint allocatedSize)
        {
            _store = store;
            _storageOverhead = storeOverhead;
            _index = index;
            _parent = parent;
            _header = new IndexHeader(allocatedSize);
            _totalSpaceAvailable = allocatedSize;

            IndexEntry endEntry = new IndexEntry(_index.IsFileIndex);
            endEntry.Flags |= IndexEntryFlags.End;

            _entries = new List<IndexEntry>();
            _entries.Add(endEntry);
        }

        public IndexNode(IndexNodeSaveFn store, int storeOverhead, Index index, IndexNode parent, byte[] buffer, int offset)
        {
            _store = store;
            _storageOverhead = storeOverhead;
            _index = index;
            _parent = parent;
            _header = new IndexHeader(buffer, offset + 0);
            _totalSpaceAvailable = _header.AllocatedSizeOfEntries;

            _entries = new List<IndexEntry>();
            int pos = (int)_header.OffsetToFirstEntry;
            while (pos < _header.TotalSizeOfEntries)
            {
                IndexEntry entry = new IndexEntry(index.IsFileIndex);
                entry.Read(buffer, offset + pos);
                _entries.Add(entry);

                if ((entry.Flags & IndexEntryFlags.End) != 0)
                {
                    break;
                }

                pos += entry.Size;
            }
        }

        public IndexHeader Header
        {
            get { return _header; }
        }

        public List<IndexEntry> Entries
        {
            get { return _entries; }
        }

        internal long TotalSpaceAvailable
        {
            get { return _totalSpaceAvailable; }
            set { _totalSpaceAvailable = value; }
        }

        public void AddEntry(byte[] key, byte[] data)
        {
            AddEntry(new IndexEntry(key, data, _index.IsFileIndex), false);
        }

        public void UpdateEntry(byte[] key, byte[] data)
        {
            for (int i = 0; i < _entries.Count; ++i)
            {
                var focus = _entries[i];
                int compVal = _index.Compare(key, focus.KeyBuffer);
                if (compVal == 0)
                {
                    IndexEntry newEntry = new IndexEntry(focus, key, data);
                    if (_entries[i].Size != newEntry.Size)
                    {
                        throw new NotImplementedException("Changing index entry sizes");
                    }
                    _entries[i] = newEntry;
                    _store();
                    return;
                }
            }

            throw new IOException("No such index entry");
        }

        public bool TryFindEntry(byte[] key, out IndexEntry entry, out IndexNode node)
        {
            foreach (var focus in _entries)
            {
                if ((focus.Flags & IndexEntryFlags.End) != 0)
                {
                    if ((focus.Flags & IndexEntryFlags.Node) != 0)
                    {
                        IndexBlock subNode = _index.GetSubBlock(this, focus);
                        return subNode.Node.TryFindEntry(key, out entry, out node);
                    }
                    break;
                }
                else
                {
                    int compVal = _index.Compare(key, focus.KeyBuffer);
                    if (compVal == 0)
                    {
                        entry = focus;
                        node = this;
                        return true;
                    }
                    else if (compVal < 0 && (focus.Flags & (IndexEntryFlags.End | IndexEntryFlags.Node)) != 0)
                    {
                        IndexBlock subNode = _index.GetSubBlock(this, focus);
                        return subNode.Node.TryFindEntry(key, out entry, out node);
                    }
                }
            }

            entry = null;
            node = null;
            return false;
        }

        public virtual ushort WriteTo(byte[] buffer, int offset)
        {
            bool haveSubNodes = false;
            uint totalEntriesSize = 0;
            foreach (var entry in _entries)
            {
                totalEntriesSize += (uint)entry.Size;
                haveSubNodes |= ((entry.Flags & IndexEntryFlags.Node) != 0);
            }

            _header.OffsetToFirstEntry = (uint)Utilities.RoundUp(IndexHeader.Size + _storageOverhead, 8);
            _header.TotalSizeOfEntries = totalEntriesSize + _header.OffsetToFirstEntry;
            _header.HasChildNodes = (byte)(haveSubNodes ? 1 : 0);
            _header.WriteTo(buffer, offset + 0);

            int pos = (int)_header.OffsetToFirstEntry;
            foreach (var entry in _entries)
            {
                entry.WriteTo(buffer, offset + pos);
                pos += entry.Size;
            }

            return IndexHeader.Size;
        }

        public virtual int CalcSize()
        {
            int totalEntriesSize = 0;
            foreach (var entry in _entries)
            {
                totalEntriesSize += entry.Size;
            }

            int firstEntryOffset = Utilities.RoundUp(IndexHeader.Size + _storageOverhead, 8);
            return firstEntryOffset + totalEntriesSize;
        }

        private long SpaceFree
        {
            get
            {
                long entriesTotal = 0;
                for (int i = 0; i < _entries.Count; ++i)
                {
                    entriesTotal += _entries[i].Size;
                }

                int firstEntryOffset = Utilities.RoundUp(IndexHeader.Size + _storageOverhead, 8);

                return _totalSpaceAvailable - (entriesTotal + firstEntryOffset);
            }
        }

        private void AddEntry(IndexEntry newEntry, bool promoting)
        {
            for (int i = 0; i < _entries.Count; ++i)
            {
                var focus = _entries[i];
                int compVal;

                if ((focus.Flags & IndexEntryFlags.End) != 0)
                {
                    // No value when End flag is set.  Logically these nodes always
                    // compare 'bigger', so if there are children we'll visit them.
                    compVal = -1;
                }
                else
                {
                    compVal = _index.Compare(newEntry.KeyBuffer, focus.KeyBuffer);
                }

                if (compVal == 0)
                {
                    throw new InvalidOperationException("Entry already exists");
                }
                else if (compVal < 0)
                {
                    if (!promoting && (focus.Flags & IndexEntryFlags.Node) != 0)
                    {
                        _index.GetSubBlock(this, focus).Node.AddEntry(newEntry, false);
                    }
                    else
                    {
                        _entries.Insert(i, newEntry);

                        if (SpaceFree < 0)
                        {
                            // The node is too small to hold the entry, so need to juggle...

                            if (_parent != null)
                            {
                                Divide();
                            }
                            else
                            {
                                Depose();
                            }
                        }

                        _store();
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Only valid on the root node, this method moves all entries into a
        /// single child node.
        /// </summary>
        private void Depose()
        {
            IndexEntry newRootEntry = new IndexEntry(_index.IsFileIndex);
            newRootEntry.Flags = IndexEntryFlags.End;

            IndexBlock newBlock = _index.AllocateBlock(this, newRootEntry);
            newBlock.Node.SetEntries(_entries, 0, _entries.Count);

            _entries.Clear();
            _entries.Add(newRootEntry);
        }

        /// <summary>
        /// Only valid on non-root nodes, this method divides the node in two,
        /// adding the new node to the current parent.
        /// </summary>
        private void Divide()
        {
            int midEntryIdx = _entries.Count / 2;
            IndexEntry midEntry = _entries[midEntryIdx];

            // The terminating entry (aka end) for the new node
            IndexEntry newTerm = new IndexEntry(_index.IsFileIndex);
            newTerm.Flags |= IndexEntryFlags.End;

            // The set of entries in the new node
            List<IndexEntry> newEntries = new List<IndexEntry>(midEntryIdx + 1);
            for (int i = 0; i < midEntryIdx; ++i)
            {
                newEntries.Add(_entries[i]);
            }
            newEntries.Add(newTerm);

            // Copy the node pointer from the elected 'mid' entry to the new node
            if ((midEntry.Flags & IndexEntryFlags.Node) != 0)
            {
                newTerm.ChildrenVirtualCluster = midEntry.ChildrenVirtualCluster;
                newTerm.Flags |= IndexEntryFlags.Node;
            }

            // Set the new entries into the new node
            IndexBlock newBlock = _index.AllocateBlock(_parent, midEntry);
            newBlock.Node.SetEntries(newEntries, 0, newEntries.Count);

            // Forget about the entries moved into the new node, and the entry about
            // to be promoted as the new node's pointer
            _entries.RemoveRange(0, midEntryIdx + 1);

            // Promote the old mid entry
            _parent.AddEntry(midEntry, true);
        }

        private void SetEntries(IList<IndexEntry> newEntries, int offset, int count)
        {
            _entries.Clear();
            for (int i = 0; i < count; ++i)
            {
                _entries.Add(newEntries[i + offset]);
            }

            // Add an end entry, if not present
            if (count == 0 || (_entries[_entries.Count - 1].Flags & IndexEntryFlags.End) == 0)
            {
                IndexEntry end = new IndexEntry(_index.IsFileIndex);
                end.Flags = IndexEntryFlags.End;
                _entries.Add(end);
            }

            // Persist the new entries to disk
            _store();
        }

    }
}