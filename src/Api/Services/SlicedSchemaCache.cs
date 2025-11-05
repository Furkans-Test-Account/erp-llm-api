// File: src/Api/Services/SlicedSchemaCache.cs
#nullable enable
using System.Threading;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    public sealed class SlicedSchemaCache : ISlicedSchemaCache
    {
        private SliceResultDto? _slice;
        private DepartmentSliceResultDto? _dept;
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

        public void Set(SliceResultDto slice)
        {
            _lock.EnterWriteLock();
            try { _slice = slice; }
            finally { _lock.ExitWriteLock(); }
        }

        public bool TryGet(out SliceResultDto? slice)
        {
            _lock.EnterReadLock();
            try { slice = _slice; return slice != null; }
            finally { _lock.ExitReadLock(); }
        }

        public void SetDepartment(DepartmentSliceResultDto dept)
        {
            _lock.EnterWriteLock();
            try { _dept = dept; }
            finally { _lock.ExitWriteLock(); }
        }

        public bool TryGetDepartment(out DepartmentSliceResultDto? dept)
        {
            _lock.EnterReadLock();
            try { dept = _dept; return dept != null; }
            finally { _lock.ExitReadLock(); }
        }
    }
}
