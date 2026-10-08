using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace OpenOita.V2
{
    /// <summary>
    /// Owns stable 16 KiB material pages and backs them with shared 64-page slabs.
    /// Child grids can borrow the parent pool, so a short-lived 128-cell body does
    /// not reserve a private 1 MiB slab. Returned pages are cleared before reuse.
    /// </summary>
    public unsafe sealed class MaterialPagePool : IDisposable
    {
        public const int PageBytes = 16384;
        public const int PagesPerSlab = 64;

        private readonly List<IntPtr> _slabs = new List<IntPtr>();
        private readonly Stack<IntPtr> _freePages = new Stack<IntPtr>(PagesPerSlab);
        private readonly HashSet<IntPtr> _ownedPages = new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> _freePageSet = new HashSet<IntPtr>();
        private int _inUse;
        private bool _disposed;

        public int SlabCount => _slabs.Count;
        public int CapacityPages => _ownedPages.Count;
        public int FreePageCount => _freePages.Count;
        public int InUsePageCount => _inUse;

        public IntPtr Rent()
        {
            EnsureUsable();
            IntPtr page;
            if (_freePages.Count != 0)
            {
                page = _freePages.Pop();
                _freePageSet.Remove(page);
            }
            else
            {
                EnsureSlab();
                IntPtr slab = _slabs[_slabs.Count - 1];
                int offset = _ownedPages.Count % PagesPerSlab;
                page = IntPtr.Add(slab, offset * PageBytes);
                _ownedPages.Add(page);
            }
            UnsafeUtility.MemClear((void*)page, PageBytes);
            _inUse++;
            return page;
        }

        public void Return(IntPtr page)
        {
            EnsureUsable();
            if (page == IntPtr.Zero || !_ownedPages.Contains(page))
                throw new ArgumentException("页不属于此MaterialPagePool。", nameof(page));
            if (!_freePageSet.Add(page))
                throw new InvalidOperationException("MaterialPagePool页被重复归还。");
            UnsafeUtility.MemClear((void*)page, PageBytes);
            _freePages.Push(page);
            _inUse--;
        }

        private void EnsureSlab()
        {
            IntPtr slab = (IntPtr)UnsafeUtility.Malloc(PageBytes * PagesPerSlab, 64, Allocator.Persistent);
            if (slab == IntPtr.Zero) throw new OutOfMemoryException("MaterialPagePool无法分配页slab。");
            _slabs.Add(slab);
            // The first slab page is handed out by Rent.  Remaining slots are
            // registered as free pages so later grids can reuse them immediately.
            for (int i = PagesPerSlab - 1; i >= 0; i--)
            {
                IntPtr page = IntPtr.Add(slab, i * PageBytes);
                _ownedPages.Add(page);
                if (i != 0)
                {
                    _freePages.Push(page);
                    _freePageSet.Add(page);
                }
            }
        }

        private void EnsureUsable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterialPagePool));
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_inUse != 0) throw new InvalidOperationException("MaterialPagePool仍有页被借用。");
            _disposed = true;
            for (int i = 0; i < _slabs.Count; i++)
                UnsafeUtility.Free((void*)_slabs[i], Allocator.Persistent);
            _slabs.Clear();
            _freePages.Clear(); _freePageSet.Clear(); _ownedPages.Clear();
        }
    }
}
