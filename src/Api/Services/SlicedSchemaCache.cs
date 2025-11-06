// File: src/Api/Services/SlicedSchemaCache.cs
#nullable enable
using System.Threading;
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services
{
    public sealed class SlicedSchemaCache : ISlicedSchemaCache
    {
        private DepartmentSliceResultDto? _dept;
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

        // Legacy no-op implementations to retire SliceResultDto-based cache
        public void Set(SliceResultDto slice) { /* no-op */ }
        public bool TryGet(out SliceResultDto? slice) { slice = null; return false; }

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
