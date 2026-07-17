//! Windows-only secure storage primitives shared by account and history stores.
//!
//! Path-taking helpers intentionally permit ancestor reparse points. Their caller
//! must anchor every path beneath the trusted per-user `dirs::data_dir` parent;
//! this module validates only the final component. Path identity guarantees are
//! point-in-time: callers must keep using the returned handle and revalidate
//! before any later pathname operation.

use std::ffi::c_void;
use std::fs::File;
use std::io;
use std::mem::size_of;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle};
use std::path::Path;
use std::ptr::{self, null, null_mut};
use std::slice;

use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, LocalFree, ERROR_ALREADY_EXISTS, ERROR_INSUFFICIENT_BUFFER,
    ERROR_SUCCESS, GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE, NTSTATUS,
};
#[cfg(test)]
use windows_sys::Win32::Security::Authorization::SetSecurityInfo;
use windows_sys::Win32::Security::Authorization::{GetSecurityInfo, SE_FILE_OBJECT};
use windows_sys::Win32::Security::Cryptography::{
    BCryptGenRandom, BCRYPT_USE_SYSTEM_PREFERRED_RNG,
};
#[cfg(test)]
use windows_sys::Win32::Security::PROTECTED_DACL_SECURITY_INFORMATION;
use windows_sys::Win32::Security::{
    AclSizeInformation, AddAccessAllowedAceEx, CopySid, CreateWellKnownSid, GetAce,
    GetAclInformation, GetLengthSid, GetSecurityDescriptorControl, GetSecurityDescriptorDacl,
    GetSecurityDescriptorLength, GetTokenInformation, InitializeAcl, InitializeSecurityDescriptor,
    IsValidAcl, IsValidSecurityDescriptor, IsValidSid, SetSecurityDescriptorControl,
    SetSecurityDescriptorDacl, SetSecurityDescriptorOwner, TokenUser, WinLocalSystemSid,
    ACCESS_ALLOWED_ACE, ACE_HEADER, ACL, ACL_REVISION, ACL_SIZE_INFORMATION,
    DACL_SECURITY_INFORMATION, NO_INHERITANCE, OWNER_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR,
    PSID, SECURITY_ATTRIBUTES, SECURITY_DESCRIPTOR, SE_DACL_PROTECTED, TOKEN_QUERY, TOKEN_USER,
    WELL_KNOWN_SID_TYPE,
};
use windows_sys::Win32::Storage::FileSystem::{
    CreateDirectoryW, CreateFileW, FileAttributeTagInfo, FileIdInfo, FlushFileBuffers,
    GetFileInformationByHandleEx, GetFileType, CREATE_NEW, FILE_ALL_ACCESS, FILE_ATTRIBUTE_DEVICE,
    FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_REPARSE_POINT,
    FILE_ATTRIBUTE_TAG_INFO, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
    FILE_ID_INFO, FILE_READ_ATTRIBUTES, FILE_READ_DATA, FILE_SHARE_DELETE, FILE_SHARE_READ,
    FILE_SHARE_WRITE, FILE_TYPE_DISK, OPEN_ALWAYS, OPEN_EXISTING, READ_CONTROL,
};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

const ACCESS_ALLOWED_ACE_TYPE_VALUE: u8 = 0;
const SID_HEADER_LEN: usize = 8;
const ACE_SID_OFFSET: usize = size_of::<ACE_HEADER>() + size_of::<u32>();
const SECURITY_DESCRIPTOR_REVISION_VALUE: u32 = 1;
const STORAGE_SHARE_MODE: u32 = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
const VALIDATION_SHARE_MODE: u32 = FILE_SHARE_READ | FILE_SHARE_WRITE;
const STORAGE_SECURITY_ACCESS: u32 = READ_CONTROL;

#[derive(Clone, Copy, Eq, PartialEq)]
struct StorageIdentity {
    volume_serial_number: u64,
    file_id: [u8; 16],
}

#[derive(Clone, Copy)]
enum StorageObjectKind {
    Directory,
    RegularFile,
}

impl StorageObjectKind {
    fn is_directory(self) -> bool {
        matches!(self, Self::Directory)
    }

    fn open_flags(self) -> u32 {
        let type_flag = if self.is_directory() {
            FILE_FLAG_BACKUP_SEMANTICS
        } else {
            FILE_ATTRIBUTE_NORMAL
        };
        type_flag | FILE_FLAG_OPEN_REPARSE_POINT
    }
}

/// Create only the final directory component securely when absent, then open and
/// verify that exact directory without following a final-component reparse point.
///
/// The caller must place `path` beneath the trusted per-user `dirs::data_dir`
/// parent. Ancestor reparse points are allowed and are not ACL-validated here.
#[allow(dead_code)] // Stage 2A2 primitive; production callers are wired later.
pub(crate) fn ensure_secure_storage_directory(path: &Path) -> io::Result<File> {
    ensure_secure_storage_directory_with(path, |_| Ok(()))
}

fn ensure_secure_storage_directory_with(
    path: &Path,
    before_verify: impl FnOnce(&File) -> io::Result<()>,
) -> io::Result<File> {
    let wide = wide_path(path)?;
    let mut security = CreationSecurity::new()?;
    let attributes = security.attributes();
    if unsafe { CreateDirectoryW(wide.as_ptr(), &attributes) } == 0 {
        let status = unsafe { GetLastError() };
        if status != ERROR_ALREADY_EXISTS {
            return Err(win32_status_error(status));
        }
    }

    open_secure_storage_object_with(
        path,
        GENERIC_WRITE | FILE_READ_ATTRIBUTES | STORAGE_SECURITY_ACCESS,
        OPEN_EXISTING,
        StorageObjectKind::Directory,
        before_verify,
    )
}

/// Create a new secure regular file opened for reading and writing.
/// The caller must anchor `path` beneath the trusted per-user data parent.
#[allow(dead_code)] // Stage 2A2 primitive; production callers are wired later.
pub(crate) fn create_new_secure_file(path: &Path) -> io::Result<File> {
    open_secure_storage_object(
        path,
        GENERIC_READ | GENERIC_WRITE | STORAGE_SECURITY_ACCESS,
        CREATE_NEW,
        StorageObjectKind::RegularFile,
    )
}

/// Open an existing regular file only when it already satisfies the exact
/// owner/DACL contract. This helper never tightens an existing object in place:
/// changing its DACL cannot revoke access already granted to retained handles.
/// The caller must anchor `path` beneath the trusted per-user data parent.
#[allow(dead_code)] // Stage 2A2 primitive; production callers are wired later.
pub(crate) fn open_existing_secure_file(path: &Path, writable: bool) -> io::Result<File> {
    let data_access = if writable {
        GENERIC_READ | GENERIC_WRITE
    } else {
        GENERIC_READ
    };
    open_secure_storage_object(
        path,
        data_access | STORAGE_SECURITY_ACCESS,
        OPEN_EXISTING,
        StorageObjectKind::RegularFile,
    )
}

/// Create a secure regular file when absent, or open it only when the existing
/// object already satisfies the exact owner/DACL contract. Never repairs in place.
/// The caller must anchor `path` beneath the trusted per-user data parent.
#[allow(dead_code)] // Stage 2A2 primitive; production callers are wired later.
pub(crate) fn open_or_create_secure_file(path: &Path) -> io::Result<File> {
    open_secure_storage_object(
        path,
        GENERIC_READ | GENERIC_WRITE | STORAGE_SECURITY_ACCESS,
        OPEN_ALWAYS,
        StorageObjectKind::RegularFile,
    )
}

/// Verify that a currently open secure regular file names the same non-reparse
/// object at `path` during this call. This function never mutates either object.
/// The guarantee is point-in-time only: keep using `file`, and call this again
/// immediately before any later operation that must act on the pathname.
#[allow(dead_code)] // Stage 2A2 primitive; production callers are wired later.
pub(crate) fn verify_secure_file_path(file: &File, path: &Path) -> io::Result<()> {
    let handle = file.as_raw_handle() as HANDLE;
    let identity = storage_identity(handle, StorageObjectKind::RegularFile)?;
    verify_storage_handle(handle)?;
    verify_path_identity(path, StorageObjectKind::RegularFile, identity)
}

/// Durably flush a verified secure storage directory handle.
#[allow(dead_code)] // Stage 2A2 primitive; production callers are wired later.
pub(crate) fn flush_secure_storage_directory(directory: &File) -> io::Result<()> {
    flush_storage_directory_handle(directory.as_raw_handle() as HANDLE)
}

fn open_secure_storage_object(
    path: &Path,
    access: u32,
    disposition: u32,
    kind: StorageObjectKind,
) -> io::Result<File> {
    open_secure_storage_object_with(path, access, disposition, kind, |_| Ok(()))
}

fn open_secure_storage_object_with(
    path: &Path,
    access: u32,
    disposition: u32,
    kind: StorageObjectKind,
    before_verify: impl FnOnce(&File) -> io::Result<()>,
) -> io::Result<File> {
    let file = open_windows_path(path, access, disposition, kind.open_flags())?;
    let identity = storage_identity(file.as_raw_handle() as HANDLE, kind)?;
    before_verify(&file)?;

    // Existing objects are accepted only when they already satisfy the exact
    // contract. Never repair a permissive DACL in place: an access check that
    // granted another process a handle remains effective after the DACL changes.
    verify_storage_handle(file.as_raw_handle() as HANDLE)?;
    verify_path_identity(path, kind, identity)?;
    Ok(file)
}

fn verify_path_identity(
    path: &Path,
    kind: StorageObjectKind,
    expected: StorageIdentity,
) -> io::Result<()> {
    verify_path_identity_with(path, kind, expected, |_| Ok(()))
}

fn verify_path_identity_with(
    path: &Path,
    kind: StorageObjectKind,
    expected: StorageIdentity,
    while_validation_open: impl FnOnce(&File) -> io::Result<()>,
) -> io::Result<()> {
    // FILE_READ_DATA (FILE_LIST_DIRECTORY for directories) makes this a counted
    // read open, so omitting FILE_SHARE_DELETE blocks competing DELETE-access
    // opens for the validation lifetime. The primary data handle still shares
    // delete for later, separately revalidated replacement.
    let validation = open_windows_path_with_share(
        path,
        FILE_READ_DATA | FILE_READ_ATTRIBUTES | READ_CONTROL,
        OPEN_EXISTING,
        kind.open_flags(),
        VALIDATION_SHARE_MODE,
    )?;
    let actual = storage_identity(validation.as_raw_handle() as HANDLE, kind)?;
    verify_storage_handle(validation.as_raw_handle() as HANDLE)?;
    while_validation_open(&validation)?;
    if actual == expected {
        Ok(())
    } else {
        Err(security_verification_failed())
    }
}

fn open_windows_path(path: &Path, access: u32, disposition: u32, flags: u32) -> io::Result<File> {
    open_windows_path_with_share(path, access, disposition, flags, STORAGE_SHARE_MODE)
}

fn open_windows_path_with_share(
    path: &Path,
    access: u32,
    disposition: u32,
    flags: u32,
    share_mode: u32,
) -> io::Result<File> {
    let wide = wide_path(path)?;
    let handle = match disposition {
        OPEN_EXISTING => unsafe {
            CreateFileW(
                wide.as_ptr(),
                access,
                share_mode,
                null(),
                disposition,
                flags,
                null_mut(),
            )
        },
        CREATE_NEW | OPEN_ALWAYS => {
            let mut security = CreationSecurity::new()?;
            let attributes = security.attributes();
            unsafe {
                CreateFileW(
                    wide.as_ptr(),
                    access,
                    share_mode,
                    &attributes,
                    disposition,
                    flags,
                    null_mut(),
                )
            }
        }
        _ => return Err(security_operation_failed()),
    };
    if handle == INVALID_HANDLE_VALUE {
        return Err(last_win32_error());
    }
    if handle.is_null() {
        return Err(security_operation_failed());
    }

    Ok(unsafe { File::from_raw_handle(handle as _) })
}

fn wide_path(path: &Path) -> io::Result<Vec<u16>> {
    let mut wide: Vec<u16> = path.as_os_str().encode_wide().collect();
    if wide.contains(&0) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid Windows storage path",
        ));
    }
    wide.push(0);
    Ok(wide)
}

fn storage_identity(handle: HANDLE, kind: StorageObjectKind) -> io::Result<StorageIdentity> {
    validate_handle(handle)?;
    if unsafe { GetFileType(handle) } != FILE_TYPE_DISK {
        return Err(security_verification_failed());
    }

    let mut attributes = FILE_ATTRIBUTE_TAG_INFO::default();
    if unsafe {
        GetFileInformationByHandleEx(
            handle,
            FileAttributeTagInfo,
            (&mut attributes as *mut FILE_ATTRIBUTE_TAG_INFO).cast(),
            size_of::<FILE_ATTRIBUTE_TAG_INFO>() as u32,
        )
    } == 0
    {
        return Err(last_win32_error());
    }
    let is_directory = attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY != 0;
    if attributes.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DEVICE) != 0
        || is_directory != kind.is_directory()
    {
        return Err(security_verification_failed());
    }

    let mut identity = FILE_ID_INFO::default();
    if unsafe {
        GetFileInformationByHandleEx(
            handle,
            FileIdInfo,
            (&mut identity as *mut FILE_ID_INFO).cast(),
            size_of::<FILE_ID_INFO>() as u32,
        )
    } == 0
    {
        return Err(last_win32_error());
    }

    Ok(StorageIdentity {
        volume_serial_number: identity.VolumeSerialNumber,
        file_id: identity.FileId.Identifier,
    })
}

fn flush_storage_directory_handle(handle: HANDLE) -> io::Result<()> {
    storage_identity(handle, StorageObjectKind::Directory)?;
    verify_storage_handle(handle)?;
    if unsafe { FlushFileBuffers(handle) } == 0 {
        Err(last_win32_error())
    } else {
        Ok(())
    }
}

/// Fill a newly allocated buffer with the Windows system-preferred CNG RNG.
/// No fallback is permitted: an NTSTATUS failure returns no bytes.
#[allow(dead_code)] // Stage 2A1 primitive; production callers are wired later.
pub(crate) fn cng_random_bytes(len: usize) -> io::Result<Vec<u8>> {
    random_bytes_with(len, |buffer, buffer_len| unsafe {
        BCryptGenRandom(
            null_mut(),
            buffer,
            buffer_len,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    })
}

fn random_bytes_with(
    len: usize,
    fill: impl FnOnce(*mut u8, u32) -> NTSTATUS,
) -> io::Result<Vec<u8>> {
    let len_u32 = u32::try_from(len).map_err(|_| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "requested random buffer is too large",
        )
    })?;
    let mut bytes = vec![0u8; len];
    if bytes.is_empty() {
        return Ok(bytes);
    }

    // BCrypt's BCRYPT_SUCCESS macro is the standard NT_SUCCESS(status >= 0)
    // predicate. On failure, clear the local buffer before returning only Err.
    if fill(bytes.as_mut_ptr(), len_u32) < 0 {
        bytes.fill(0);
        return Err(io::Error::other("secure random generation failed"));
    }
    Ok(bytes)
}

/// Verify without modifying a file or directory handle's owner or DACL.
#[allow(dead_code)] // Stage 2A1 primitive; production callers are wired later.
pub(crate) fn verify_storage_handle(handle: HANDLE) -> io::Result<()> {
    validate_handle(handle)?;
    let current_user = current_process_user_sid()?;
    let local_system = well_known_sid(WinLocalSystemSid)?;
    verify_storage_handle_with(handle, &current_user, &local_system)
}

fn verify_storage_handle_with(
    handle: HANDLE,
    current_user: &Sid,
    local_system: &Sid,
) -> io::Result<()> {
    let snapshot = read_security_snapshot(handle)?;
    inspect_owner(&snapshot.owner, current_user.as_bytes())?;
    inspect_acl(
        &snapshot.acl,
        current_user.as_bytes(),
        local_system.as_bytes(),
    )
}

fn inspect_owner(owner: &[u8], current_user: &[u8]) -> io::Result<()> {
    if !owner.is_empty() && owner == current_user {
        Ok(())
    } else {
        Err(security_verification_failed())
    }
}

fn validate_handle(handle: HANDLE) -> io::Result<()> {
    if handle.is_null() || handle == INVALID_HANDLE_VALUE {
        Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid Windows storage handle",
        ))
    } else {
        Ok(())
    }
}

fn expected_principals<'a>(current_user: &'a Sid, local_system: &'a Sid) -> Vec<&'a Sid> {
    if current_user.as_bytes() == local_system.as_bytes() {
        vec![current_user]
    } else {
        vec![current_user, local_system]
    }
}

struct AlignedBuffer {
    words: Vec<usize>,
    byte_len: usize,
}

impl AlignedBuffer {
    fn zeroed(byte_len: usize) -> io::Result<Self> {
        if byte_len == 0 {
            return Err(security_operation_failed());
        }
        let word_size = size_of::<usize>();
        let word_count = byte_len
            .checked_add(word_size - 1)
            .ok_or_else(security_operation_failed)?
            / word_size;
        Ok(Self {
            words: vec![0usize; word_count],
            byte_len,
        })
    }

    fn as_ptr(&self) -> *const u8 {
        self.words.as_ptr().cast()
    }

    fn as_mut_ptr(&mut self) -> *mut u8 {
        self.words.as_mut_ptr().cast()
    }

    fn len(&self) -> usize {
        self.byte_len
    }
}

struct Sid {
    buffer: AlignedBuffer,
    byte_len: usize,
}

impl Sid {
    fn zeroed(byte_len: usize) -> io::Result<Self> {
        Ok(Self {
            buffer: AlignedBuffer::zeroed(byte_len)?,
            byte_len,
        })
    }

    fn as_psid(&self) -> PSID {
        self.buffer.as_ptr() as PSID
    }

    fn as_mut_psid(&mut self) -> PSID {
        self.buffer.as_mut_ptr().cast()
    }

    fn as_bytes(&self) -> &[u8] {
        unsafe { slice::from_raw_parts(self.buffer.as_ptr(), self.byte_len) }
    }
}

struct OwnedHandle(HANDLE);

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}

struct LocalAllocation(*mut c_void);

impl Drop for LocalAllocation {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                let _ = LocalFree(self.0);
            }
        }
    }
}

fn current_process_user_sid() -> io::Result<Sid> {
    let mut token = null_mut();
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return Err(last_win32_error());
    }
    if token.is_null() || token == INVALID_HANDLE_VALUE {
        return Err(security_operation_failed());
    }
    let token = OwnedHandle(token);

    let mut required = 0u32;
    let first = unsafe { GetTokenInformation(token.0, TokenUser, null_mut(), 0, &mut required) };
    let first_error = unsafe { GetLastError() };
    if first != 0
        || first_error != ERROR_INSUFFICIENT_BUFFER
        || required < size_of::<TOKEN_USER>() as u32
    {
        return Err(security_operation_failed());
    }

    let mut token_buffer = AlignedBuffer::zeroed(required as usize)?;
    let mut returned = 0u32;
    if unsafe {
        GetTokenInformation(
            token.0,
            TokenUser,
            token_buffer.as_mut_ptr().cast(),
            required,
            &mut returned,
        )
    } == 0
    {
        return Err(last_win32_error());
    }
    if returned < size_of::<TOKEN_USER>() as u32 || returned > required {
        return Err(security_operation_failed());
    }

    let token_user = unsafe { ptr::read_unaligned(token_buffer.as_ptr().cast::<TOKEN_USER>()) };
    copy_sid_from_bounded(
        token_user.User.Sid,
        token_buffer.as_ptr(),
        returned as usize,
    )
}

fn well_known_sid(kind: WELL_KNOWN_SID_TYPE) -> io::Result<Sid> {
    let mut required = 0u32;
    let first = unsafe { CreateWellKnownSid(kind, null_mut(), null_mut(), &mut required) };
    let first_error = unsafe { GetLastError() };
    if first != 0 || first_error != ERROR_INSUFFICIENT_BUFFER || required == 0 {
        return Err(security_operation_failed());
    }

    let mut sid = Sid::zeroed(required as usize)?;
    let mut returned = required;
    if unsafe { CreateWellKnownSid(kind, null_mut(), sid.as_mut_psid(), &mut returned) } == 0 {
        return Err(last_win32_error());
    }
    if returned == 0 || returned > required {
        return Err(security_operation_failed());
    }

    let validated = validate_sid_within(sid.as_psid(), sid.buffer.as_ptr(), returned as usize)?;
    if validated != returned as usize {
        return Err(security_operation_failed());
    }
    sid.byte_len = validated;
    Ok(sid)
}

fn copy_sid_from_bounded(source: PSID, base: *const u8, available: usize) -> io::Result<Sid> {
    let byte_len = validate_sid_within(source, base, available)?;
    let mut copy = Sid::zeroed(byte_len)?;
    if unsafe { CopySid(byte_len as u32, copy.as_mut_psid(), source) } == 0 {
        return Err(last_win32_error());
    }
    if validate_sid_within(copy.as_psid(), copy.buffer.as_ptr(), copy.buffer.len())? != byte_len {
        return Err(security_operation_failed());
    }
    Ok(copy)
}

fn validate_sid_within(source: PSID, base: *const u8, available: usize) -> io::Result<usize> {
    if source.is_null() || base.is_null() {
        return Err(security_operation_failed());
    }

    let base_address = base as usize;
    let end_address = base_address
        .checked_add(available)
        .ok_or_else(security_operation_failed)?;
    let sid_address = source as usize;
    let header_end = sid_address
        .checked_add(SID_HEADER_LEN)
        .ok_or_else(security_operation_failed)?;
    if sid_address < base_address || header_end > end_address {
        return Err(security_operation_failed());
    }

    let sub_authority_count = unsafe { *(source.cast::<u8>().add(1)) } as usize;
    let byte_len = SID_HEADER_LEN
        .checked_add(
            sub_authority_count
                .checked_mul(size_of::<u32>())
                .ok_or_else(security_operation_failed)?,
        )
        .ok_or_else(security_operation_failed)?;
    let sid_end = sid_address
        .checked_add(byte_len)
        .ok_or_else(security_operation_failed)?;
    if sid_end > end_address || unsafe { IsValidSid(source) } == 0 {
        return Err(security_operation_failed());
    }
    if unsafe { GetLengthSid(source) } as usize != byte_len {
        return Err(security_operation_failed());
    }
    Ok(byte_len)
}

struct AclBuffer(AlignedBuffer);

impl AclBuffer {
    fn as_ptr(&self) -> *const ACL {
        self.0.as_ptr().cast()
    }
}

fn build_full_control_acl(principals: &[&Sid]) -> io::Result<AclBuffer> {
    if principals.is_empty() {
        return Err(security_operation_failed());
    }

    let ace_prefix_len = size_of::<ACCESS_ALLOWED_ACE>() - size_of::<u32>();
    let mut acl_len = size_of::<ACL>();
    for principal in principals {
        acl_len = acl_len
            .checked_add(
                ace_prefix_len
                    .checked_add(principal.byte_len)
                    .ok_or_else(security_operation_failed)?,
            )
            .ok_or_else(security_operation_failed)?;
    }
    if acl_len > u16::MAX as usize {
        return Err(security_operation_failed());
    }

    let mut buffer = AlignedBuffer::zeroed(acl_len)?;
    let acl = buffer.as_mut_ptr().cast::<ACL>();
    if unsafe { InitializeAcl(acl, acl_len as u32, ACL_REVISION) } == 0 {
        return Err(last_win32_error());
    }
    for principal in principals {
        if unsafe {
            AddAccessAllowedAceEx(
                acl,
                ACL_REVISION,
                NO_INHERITANCE,
                FILE_ALL_ACCESS,
                principal.as_psid(),
            )
        } == 0
        {
            return Err(last_win32_error());
        }
    }
    if unsafe { IsValidAcl(acl) } == 0 {
        return Err(security_operation_failed());
    }
    Ok(AclBuffer(buffer))
}

struct CreationSecurity {
    // These allocations back pointers stored in the absolute descriptor.
    _owner: Sid,
    _acl: AclBuffer,
    descriptor: SECURITY_DESCRIPTOR,
}

impl CreationSecurity {
    fn new() -> io::Result<Self> {
        let owner = current_process_user_sid()?;
        let local_system = well_known_sid(WinLocalSystemSid)?;
        let principals = expected_principals(&owner, &local_system);
        let acl = build_full_control_acl(&principals)?;
        let mut descriptor = SECURITY_DESCRIPTOR::default();
        let descriptor_pointer: PSECURITY_DESCRIPTOR =
            (&mut descriptor as *mut SECURITY_DESCRIPTOR).cast();

        if unsafe {
            InitializeSecurityDescriptor(descriptor_pointer, SECURITY_DESCRIPTOR_REVISION_VALUE)
        } == 0
        {
            return Err(last_win32_error());
        }
        if unsafe { SetSecurityDescriptorOwner(descriptor_pointer, owner.as_psid(), 0) } == 0 {
            return Err(last_win32_error());
        }
        if unsafe { SetSecurityDescriptorDacl(descriptor_pointer, 1, acl.as_ptr(), 0) } == 0 {
            return Err(last_win32_error());
        }
        if unsafe {
            SetSecurityDescriptorControl(descriptor_pointer, SE_DACL_PROTECTED, SE_DACL_PROTECTED)
        } == 0
        {
            return Err(last_win32_error());
        }
        if unsafe { IsValidSecurityDescriptor(descriptor_pointer) } == 0 {
            return Err(security_operation_failed());
        }

        Ok(Self {
            _owner: owner,
            _acl: acl,
            descriptor,
        })
    }

    fn attributes(&mut self) -> SECURITY_ATTRIBUTES {
        SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: (&mut self.descriptor as *mut SECURITY_DESCRIPTOR).cast(),
            bInheritHandle: 0,
        }
    }
}

#[cfg(test)]
fn set_protected_handle_dacl(handle: HANDLE, acl: *const ACL) -> io::Result<()> {
    let status = unsafe {
        SetSecurityInfo(
            handle,
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            null_mut(),
            null_mut(),
            acl,
            null(),
        )
    };
    if status == ERROR_SUCCESS {
        Ok(())
    } else {
        Err(win32_status_error(status))
    }
}

#[derive(Clone, Eq, PartialEq)]
struct AclSnapshot {
    dacl_present: bool,
    dacl_null: bool,
    protected: bool,
    aces: Vec<AceSnapshot>,
}

#[derive(Clone, Eq, PartialEq)]
struct AceSnapshot {
    ace_type: u8,
    flags: u8,
    mask: u32,
    sid: Vec<u8>,
}

struct SecuritySnapshot {
    owner: Vec<u8>,
    acl: AclSnapshot,
}

fn read_acl_snapshot(handle: HANDLE) -> io::Result<AclSnapshot> {
    Ok(read_security_snapshot(handle)?.acl)
}

fn read_security_snapshot(handle: HANDLE) -> io::Result<SecuritySnapshot> {
    validate_handle(handle)?;

    let mut owner = null_mut();
    let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
    let status = unsafe {
        GetSecurityInfo(
            handle,
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            &mut owner,
            null_mut(),
            null_mut(),
            null_mut(),
            &mut descriptor,
        )
    };
    let descriptor_allocation = LocalAllocation(descriptor);
    if status != ERROR_SUCCESS {
        return Err(win32_status_error(status));
    }
    if descriptor_allocation.0.is_null() {
        return Err(security_operation_failed());
    }

    let descriptor_len = unsafe { GetSecurityDescriptorLength(descriptor_allocation.0) } as usize;
    if descriptor_len == 0 {
        return Err(security_operation_failed());
    }
    let owner_len =
        validate_sid_within(owner, descriptor_allocation.0.cast::<u8>(), descriptor_len)?;
    let owner = unsafe { slice::from_raw_parts(owner.cast::<u8>(), owner_len) }.to_vec();

    let mut control = 0u16;
    let mut revision = 0u32;
    if unsafe { GetSecurityDescriptorControl(descriptor_allocation.0, &mut control, &mut revision) }
        == 0
    {
        return Err(last_win32_error());
    }

    let mut dacl_present = 0;
    let mut dacl = null_mut();
    let mut dacl_defaulted = 0;
    if unsafe {
        GetSecurityDescriptorDacl(
            descriptor_allocation.0,
            &mut dacl_present,
            &mut dacl,
            &mut dacl_defaulted,
        )
    } == 0
    {
        return Err(last_win32_error());
    }

    let mut snapshot = AclSnapshot {
        dacl_present: dacl_present != 0,
        dacl_null: dacl.is_null(),
        protected: control & SE_DACL_PROTECTED != 0,
        aces: Vec::new(),
    };
    if !snapshot.dacl_present || snapshot.dacl_null {
        return Ok(SecuritySnapshot {
            owner,
            acl: snapshot,
        });
    }
    let descriptor_start = descriptor_allocation.0 as usize;
    let descriptor_end = descriptor_start
        .checked_add(descriptor_len)
        .ok_or_else(security_verification_failed)?;
    let acl_start = dacl as usize;
    let acl_header_end = acl_start
        .checked_add(size_of::<ACL>())
        .ok_or_else(security_verification_failed)?;
    if acl_start < descriptor_start || acl_header_end > descriptor_end {
        return Err(security_verification_failed());
    }

    let acl_header = unsafe { ptr::read_unaligned(dacl) };
    let acl_size = acl_header.AclSize as usize;
    let allocated_acl_end = acl_start
        .checked_add(acl_size)
        .ok_or_else(security_verification_failed)?;
    if acl_size < size_of::<ACL>() || allocated_acl_end > descriptor_end {
        return Err(security_verification_failed());
    }
    if unsafe { IsValidAcl(dacl) } == 0 {
        return Err(security_verification_failed());
    }

    let mut info = ACL_SIZE_INFORMATION::default();
    if unsafe {
        GetAclInformation(
            dacl,
            (&mut info as *mut ACL_SIZE_INFORMATION).cast(),
            size_of::<ACL_SIZE_INFORMATION>() as u32,
            AclSizeInformation,
        )
    } == 0
    {
        return Err(last_win32_error());
    }

    let bytes_in_use = info.AclBytesInUse as usize;
    if bytes_in_use < size_of::<ACL>() || bytes_in_use > acl_size {
        return Err(security_verification_failed());
    }
    let acl_end = acl_start
        .checked_add(bytes_in_use)
        .ok_or_else(security_verification_failed)?;
    let first_ace = acl_header_end;

    snapshot.aces.reserve(info.AceCount as usize);
    for index in 0..info.AceCount {
        let mut ace = null_mut::<c_void>();
        if unsafe { GetAce(dacl, index, &mut ace) } == 0 {
            return Err(last_win32_error());
        }
        if ace.is_null() {
            return Err(security_verification_failed());
        }

        let ace_start = ace as usize;
        let header_end = ace_start
            .checked_add(size_of::<ACE_HEADER>())
            .ok_or_else(security_verification_failed)?;
        if ace_start < first_ace || header_end > acl_end {
            return Err(security_verification_failed());
        }
        let header = unsafe { ptr::read_unaligned(ace.cast::<ACE_HEADER>()) };
        let ace_size = header.AceSize as usize;
        let ace_end = ace_start
            .checked_add(ace_size)
            .ok_or_else(security_verification_failed)?;
        if ace_size < size_of::<ACE_HEADER>() || ace_end > acl_end {
            return Err(security_verification_failed());
        }

        if header.AceType != ACCESS_ALLOWED_ACE_TYPE_VALUE {
            snapshot.aces.push(AceSnapshot {
                ace_type: header.AceType,
                flags: header.AceFlags,
                mask: 0,
                sid: Vec::new(),
            });
            continue;
        }
        if ace_size < ACE_SID_OFFSET + SID_HEADER_LEN {
            return Err(security_verification_failed());
        }

        let ace_bytes = ace.cast::<u8>();
        let mask =
            unsafe { ptr::read_unaligned(ace_bytes.add(size_of::<ACE_HEADER>()).cast::<u32>()) };
        let sid = unsafe { ace_bytes.add(ACE_SID_OFFSET) } as PSID;
        let sid_len = validate_sid_within(sid, ace_bytes, ace_size)?;
        if ACE_SID_OFFSET + sid_len != ace_size {
            return Err(security_verification_failed());
        }
        let sid = unsafe { slice::from_raw_parts(sid.cast::<u8>(), sid_len) }.to_vec();
        snapshot.aces.push(AceSnapshot {
            ace_type: header.AceType,
            flags: header.AceFlags,
            mask,
            sid,
        });
    }

    Ok(SecuritySnapshot {
        owner,
        acl: snapshot,
    })
}

fn inspect_acl(snapshot: &AclSnapshot, current_user: &[u8], local_system: &[u8]) -> io::Result<()> {
    if current_user.is_empty()
        || local_system.is_empty()
        || !snapshot.dacl_present
        || snapshot.dacl_null
        || !snapshot.protected
    {
        return Err(security_verification_failed());
    }

    let same_principal = current_user == local_system;
    let expected_count = if same_principal { 1 } else { 2 };
    if snapshot.aces.len() != expected_count {
        return Err(security_verification_failed());
    }

    let mut current_seen = false;
    let mut system_seen = same_principal;
    for ace in &snapshot.aces {
        if ace.ace_type != ACCESS_ALLOWED_ACE_TYPE_VALUE
            || ace.flags != NO_INHERITANCE as u8
            || ace.mask != FILE_ALL_ACCESS
        {
            return Err(security_verification_failed());
        }

        if ace.sid == current_user {
            if current_seen {
                return Err(security_verification_failed());
            }
            current_seen = true;
        } else if !same_principal && ace.sid == local_system {
            if system_seen {
                return Err(security_verification_failed());
            }
            system_seen = true;
        } else {
            // This rejects every broad or foreign SID, including Users,
            // Authenticated Users, and Everyone, for both allow and deny ACEs.
            return Err(security_verification_failed());
        }
    }

    if current_seen && system_seen {
        Ok(())
    } else {
        Err(security_verification_failed())
    }
}

fn last_win32_error() -> io::Error {
    win32_status_error(unsafe { GetLastError() })
}

fn win32_status_error(status: u32) -> io::Error {
    io::Error::from_raw_os_error(status as i32)
}

fn security_operation_failed() -> io::Error {
    io::Error::other("Windows security operation failed")
}

fn security_verification_failed() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "Windows storage security verification failed",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs::{self, File, OpenOptions};
    use std::io::{Read, Seek, SeekFrom, Write};
    use std::mem;
    use std::os::windows::fs::{symlink_file, OpenOptionsExt};
    use std::os::windows::io::AsRawHandle;
    use std::path::{Path, PathBuf};
    use std::process::Command;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    use windows_sys::Win32::Foundation::{GENERIC_READ, GENERIC_WRITE};
    use windows_sys::Win32::Security::Authorization::ConvertSidToStringSidW;
    use windows_sys::Win32::Security::{WinAuthenticatedUserSid, WinWorldSid, INHERITED_ACE};
    use windows_sys::Win32::Storage::FileSystem::{DELETE, READ_CONTROL, WRITE_DAC};

    const ACCESS_DENIED_ACE_TYPE_VALUE: u8 = 1;
    static NEXT_TEMP_ID: AtomicU64 = AtomicU64::new(0);

    struct TempArtifact {
        file: Option<File>,
        path: PathBuf,
    }

    impl TempArtifact {
        fn create() -> io::Result<Self> {
            for _ in 0..32 {
                let timestamp = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_nanos();
                let sequence = NEXT_TEMP_ID.fetch_add(1, Ordering::Relaxed);
                let path = std::env::temp_dir().join(format!(
                    "tokenbar-storage-{}-{timestamp}-{sequence}.tmp",
                    std::process::id()
                ));
                let result = open_windows_path(
                    &path,
                    GENERIC_READ | GENERIC_WRITE | READ_CONTROL | WRITE_DAC,
                    CREATE_NEW,
                    FILE_ATTRIBUTE_NORMAL,
                );
                match result {
                    Ok(file) => {
                        return Ok(Self {
                            file: Some(file),
                            path,
                        });
                    }
                    Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
                    Err(error) => return Err(error),
                }
            }
            Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "unable to create temporary storage artifact",
            ))
        }

        fn handle(&self) -> HANDLE {
            self.file
                .as_ref()
                .expect("temporary file is open")
                .as_raw_handle() as HANDLE
        }

        fn cleanup(mut self) -> io::Result<()> {
            self.file.take();
            let path = mem::take(&mut self.path);
            fs::remove_file(path)
        }
    }

    impl Drop for TempArtifact {
        fn drop(&mut self) {
            self.file.take();
            if !self.path.as_os_str().is_empty() {
                let _ = fs::remove_file(&self.path);
            }
        }
    }

    struct TempRoot {
        path: PathBuf,
    }

    impl TempRoot {
        fn create() -> io::Result<Self> {
            for _ in 0..32 {
                let timestamp = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_nanos();
                let sequence = NEXT_TEMP_ID.fetch_add(1, Ordering::Relaxed);
                let path = std::env::temp_dir().join(format!(
                    "tokenbar-storage-root-{}-{timestamp}-{sequence}",
                    std::process::id()
                ));
                match fs::create_dir(&path) {
                    Ok(()) => return Ok(Self { path }),
                    Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
                    Err(error) => return Err(error),
                }
            }
            Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "unable to create temporary storage root",
            ))
        }

        fn join(&self, name: &str) -> PathBuf {
            self.path.join(name)
        }

        fn cleanup(mut self) -> io::Result<()> {
            let path = mem::take(&mut self.path);
            fs::remove_dir_all(path)
        }
    }

    impl Drop for TempRoot {
        fn drop(&mut self) {
            if !self.path.as_os_str().is_empty() {
                let _ = fs::remove_dir_all(&self.path);
            }
        }
    }

    fn create_test_file(path: &Path, contents: &[u8]) -> io::Result<File> {
        let mut file = open_windows_path(
            path,
            GENERIC_READ | GENERIC_WRITE | READ_CONTROL | WRITE_DAC,
            CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL,
        )?;
        file.write_all(contents)?;
        file.sync_all()?;
        Ok(file)
    }

    fn create_permissive_test_file(path: &Path, contents: &[u8]) -> io::Result<File> {
        let file = create_test_file(path, contents)?;
        let everyone = well_known_sid(WinWorldSid)?;
        let acl = build_full_control_acl(&[&everyone])?;
        set_protected_handle_dacl(file.as_raw_handle() as HANDLE, acl.as_ptr())?;
        Ok(file)
    }

    fn create_permissive_test_directory(path: &Path) -> io::Result<File> {
        drop(ensure_secure_storage_directory(path)?);
        let directory = open_windows_path(
            path,
            GENERIC_WRITE | FILE_READ_ATTRIBUTES | READ_CONTROL | WRITE_DAC,
            OPEN_EXISTING,
            StorageObjectKind::Directory.open_flags(),
        )?;
        let everyone = well_known_sid(WinWorldSid)?;
        let acl = build_full_control_acl(&[&everyone])?;
        set_protected_handle_dacl(directory.as_raw_handle() as HANDLE, acl.as_ptr())?;
        Ok(directory)
    }

    fn create_junction(link: &Path, target: &Path) -> io::Result<()> {
        let output = Command::new("cmd")
            .arg("/D")
            .arg("/C")
            .arg("mklink")
            .arg("/J")
            .arg(link)
            .arg(target)
            .output()?;
        if output.status.success() {
            Ok(())
        } else {
            Err(io::Error::other("unable to create test junction"))
        }
    }

    fn read_open_file(file: &mut File) -> io::Result<Vec<u8>> {
        file.seek(SeekFrom::Start(0))?;
        let mut bytes = Vec::new();
        file.read_to_end(&mut bytes)?;
        Ok(bytes)
    }

    fn sid_string(sid: &Sid) -> io::Result<String> {
        let mut wide = null_mut();
        if unsafe { ConvertSidToStringSidW(sid.as_psid(), &mut wide) } == 0 {
            return Err(last_win32_error());
        }
        let allocation = LocalAllocation(wide.cast());
        if allocation.0.is_null() {
            return Err(security_operation_failed());
        }

        let mut len = 0usize;
        while len < 256 && unsafe { *wide.add(len) } != 0 {
            len += 1;
        }
        if len == 256 {
            return Err(security_operation_failed());
        }
        String::from_utf16(unsafe { slice::from_raw_parts(wide, len) })
            .map_err(|_| security_operation_failed())
    }

    fn assert_generic_error(error: &io::Error, sensitive_values: &[&str]) {
        let message = error.to_string().to_lowercase();
        for value in sensitive_values {
            if !value.is_empty() {
                assert!(
                    !message.contains(&value.to_lowercase()),
                    "security error does not disclose sensitive context"
                );
            }
        }
    }

    #[test]
    fn cng_random_returns_requested_distinct_bytes() {
        let first = cng_random_bytes(32).expect("first CNG request succeeds");
        let second = cng_random_bytes(32).expect("second CNG request succeeds");
        assert!(first.len() == 32, "first CNG result has requested length");
        assert!(second.len() == 32, "second CNG result has requested length");
        assert!(first != second, "independent CNG requests differ");
    }

    #[test]
    fn cng_failure_returns_no_partially_filled_bytes() {
        let result = random_bytes_with(32, |buffer, buffer_len| {
            unsafe {
                ptr::write_bytes(buffer, 0xA5, buffer_len as usize);
            }
            -1
        });
        assert!(result.is_err(), "failed CNG request returns only an error");
    }

    #[test]
    fn creation_dacl_round_trips_exact_principals_and_permissions() {
        let artifact = TempArtifact::create().expect("create secure temporary file");
        verify_storage_handle(artifact.handle()).expect("creation security verifies");

        let current_user = current_process_user_sid().expect("read current process SID");
        let local_system = well_known_sid(WinLocalSystemSid).expect("create LocalSystem SID");
        let snapshot = read_acl_snapshot(artifact.handle()).expect("read temporary file DACL");
        inspect_acl(&snapshot, current_user.as_bytes(), local_system.as_bytes())
            .expect("DACL satisfies exact contract");
        let expected_count = if current_user.as_bytes() == local_system.as_bytes() {
            1
        } else {
            2
        };
        assert!(snapshot.dacl_present, "DACL is present");
        assert!(!snapshot.dacl_null, "DACL is non-null");
        assert!(snapshot.protected, "DACL is protected");
        assert!(
            snapshot.aces.len() == expected_count,
            "DACL has exactly the expected principals"
        );
        assert!(
            snapshot
                .aces
                .iter()
                .all(|ace| ace.flags & INHERITED_ACE as u8 == 0),
            "DACL has no inherited ACE"
        );

        artifact.cleanup().expect("remove temporary file");
    }

    #[test]
    fn broad_aces_fail_closed_without_in_place_repair() {
        let artifact = TempArtifact::create().expect("create temporary file");

        for kind in [WinWorldSid, WinAuthenticatedUserSid] {
            let broad_sid = well_known_sid(kind).expect("create broad well-known SID");
            let broad_acl =
                build_full_control_acl(&[&broad_sid]).expect("build permissive temporary ACL");
            set_protected_handle_dacl(artifact.handle(), broad_acl.as_ptr())
                .expect("apply permissive temporary ACL");
            let before = read_acl_snapshot(artifact.handle()).expect("snapshot broad DACL");

            assert!(
                verify_storage_handle(artifact.handle()).is_err(),
                "broad ACE is rejected"
            );
            assert!(
                read_acl_snapshot(artifact.handle()).expect("read rejected DACL") == before,
                "verification never repairs the broad DACL in place"
            );
        }

        artifact.cleanup().expect("remove temporary file");
    }

    #[test]
    fn owner_mismatch_fails_closed() {
        let current_user = current_process_user_sid().expect("read current process SID");
        let local_system = well_known_sid(WinLocalSystemSid).expect("create LocalSystem SID");
        let everyone = well_known_sid(WinWorldSid).expect("create Everyone SID");
        let foreign_owner = if current_user.as_bytes() != local_system.as_bytes() {
            &local_system
        } else {
            &everyone
        };

        inspect_owner(foreign_owner.as_bytes(), current_user.as_bytes())
            .expect_err("foreign owner is rejected");
        inspect_owner(current_user.as_bytes(), current_user.as_bytes())
            .expect("current process owner is accepted");
    }

    #[test]
    fn pure_acl_inspection_rejects_missing_null_extra_deny_and_incomplete_entries() {
        let current_user = current_process_user_sid().expect("read current process SID");
        let local_system = well_known_sid(WinLocalSystemSid).expect("create LocalSystem SID");
        let everyone = well_known_sid(WinWorldSid).expect("create Everyone SID");
        let mut aces = vec![AceSnapshot {
            ace_type: ACCESS_ALLOWED_ACE_TYPE_VALUE,
            flags: NO_INHERITANCE as u8,
            mask: FILE_ALL_ACCESS,
            sid: current_user.as_bytes().to_vec(),
        }];
        if current_user.as_bytes() != local_system.as_bytes() {
            aces.push(AceSnapshot {
                ace_type: ACCESS_ALLOWED_ACE_TYPE_VALUE,
                flags: NO_INHERITANCE as u8,
                mask: FILE_ALL_ACCESS,
                sid: local_system.as_bytes().to_vec(),
            });
        }
        let valid = AclSnapshot {
            dacl_present: true,
            dacl_null: false,
            protected: true,
            aces,
        };
        assert!(
            inspect_acl(&valid, current_user.as_bytes(), local_system.as_bytes()).is_ok(),
            "baseline ACL is accepted"
        );

        let mut missing = valid.clone();
        missing.dacl_present = false;
        assert!(
            inspect_acl(&missing, current_user.as_bytes(), local_system.as_bytes()).is_err(),
            "missing DACL is rejected"
        );

        let mut null_dacl = valid.clone();
        null_dacl.dacl_null = true;
        assert!(
            inspect_acl(&null_dacl, current_user.as_bytes(), local_system.as_bytes()).is_err(),
            "null DACL is rejected"
        );

        let mut unprotected = valid.clone();
        unprotected.protected = false;
        assert!(
            inspect_acl(
                &unprotected,
                current_user.as_bytes(),
                local_system.as_bytes()
            )
            .is_err(),
            "unprotected DACL is rejected"
        );

        let mut inherited = valid.clone();
        inherited.aces[0].flags = INHERITED_ACE as u8;
        assert!(
            inspect_acl(&inherited, current_user.as_bytes(), local_system.as_bytes()).is_err(),
            "inherited ACE is rejected"
        );

        let mut extra = valid.clone();
        extra.aces.push(AceSnapshot {
            ace_type: ACCESS_ALLOWED_ACE_TYPE_VALUE,
            flags: NO_INHERITANCE as u8,
            mask: FILE_ALL_ACCESS,
            sid: everyone.as_bytes().to_vec(),
        });
        assert!(
            inspect_acl(&extra, current_user.as_bytes(), local_system.as_bytes()).is_err(),
            "extra broad allow ACE is rejected"
        );

        let mut denied = valid.clone();
        denied.aces.push(AceSnapshot {
            ace_type: ACCESS_DENIED_ACE_TYPE_VALUE,
            flags: NO_INHERITANCE as u8,
            mask: FILE_ALL_ACCESS,
            sid: everyone.as_bytes().to_vec(),
        });
        assert!(
            inspect_acl(&denied, current_user.as_bytes(), local_system.as_bytes()).is_err(),
            "extra broad deny ACE is rejected"
        );

        let mut incomplete = valid;
        incomplete.aces[0].mask = FILE_ALL_ACCESS & !1;
        assert!(
            inspect_acl(
                &incomplete,
                current_user.as_bytes(),
                local_system.as_bytes()
            )
            .is_err(),
            "incomplete access mask is rejected"
        );
    }

    #[test]
    fn new_objects_are_secure_on_their_first_open_handle() {
        let root = TempRoot::create().expect("create temporary root");
        let storage_path = root.join("storage");
        let directory = ensure_secure_storage_directory_with(&storage_path, |created| {
            verify_storage_handle(created.as_raw_handle() as HANDLE)
        })
        .expect("directory is secure at creation time");

        let create_new_path = storage_path.join("create-new.json");
        let create_new = open_secure_storage_object_with(
            &create_new_path,
            GENERIC_READ | GENERIC_WRITE | STORAGE_SECURITY_ACCESS,
            CREATE_NEW,
            StorageObjectKind::RegularFile,
            |created| verify_storage_handle(created.as_raw_handle() as HANDLE),
        )
        .expect("CREATE_NEW file is secure on its first handle");

        let open_always_path = storage_path.join("open-always.json");
        let open_always = open_secure_storage_object_with(
            &open_always_path,
            GENERIC_READ | GENERIC_WRITE | STORAGE_SECURITY_ACCESS,
            OPEN_ALWAYS,
            StorageObjectKind::RegularFile,
            |created| verify_storage_handle(created.as_raw_handle() as HANDLE),
        )
        .expect("OPEN_ALWAYS-created file is secure on its first handle");

        drop(open_always);
        drop(create_new);
        drop(directory);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn secure_directory_and_files_round_trip_type_dacl_identity_and_bytes() {
        let root = TempRoot::create().expect("create temporary root");
        let storage_path = root.join("storage");
        let directory =
            ensure_secure_storage_directory(&storage_path).expect("create secure directory");
        storage_identity(
            directory.as_raw_handle() as HANDLE,
            StorageObjectKind::Directory,
        )
        .expect("directory type and identity verify");
        verify_storage_handle(directory.as_raw_handle() as HANDLE)
            .expect("directory DACL verifies");
        flush_secure_storage_directory(&directory).expect("directory flush succeeds");

        let file_path = storage_path.join("history.json");
        let mut file = create_new_secure_file(&file_path).expect("create secure file");
        file.write_all(b"secure history")
            .expect("write secure file");
        file.sync_all().expect("sync secure file");
        let original_identity = storage_identity(
            file.as_raw_handle() as HANDLE,
            StorageObjectKind::RegularFile,
        )
        .expect("file type and identity verify");
        verify_storage_handle(file.as_raw_handle() as HANDLE).expect("file DACL verifies");
        verify_secure_file_path(&file, &file_path).expect("file path identity verifies");
        drop(file);

        let mut reopened =
            open_existing_secure_file(&file_path, false).expect("reopen secure file read-only");
        let reopened_identity = storage_identity(
            reopened.as_raw_handle() as HANDLE,
            StorageObjectKind::RegularFile,
        )
        .expect("reopened file identity verifies");
        assert!(
            original_identity == reopened_identity,
            "reopen preserves stable file identity"
        );
        assert!(
            read_open_file(&mut reopened).expect("read reopened secure file") == b"secure history",
            "reopened bytes match"
        );
        let mut writable_reopened =
            open_existing_secure_file(&file_path, true).expect("reopen secure file read/write");
        writable_reopened
            .seek(SeekFrom::End(0))
            .expect("seek writable secure file");
        writable_reopened
            .write_all(b"!")
            .expect("write reopened secure file");
        writable_reopened
            .sync_all()
            .expect("sync reopened secure file");
        verify_secure_file_path(&writable_reopened, &file_path)
            .expect("writable file path identity verifies");
        drop(writable_reopened);
        assert!(
            read_open_file(&mut reopened).expect("reread secure file") == b"secure history!",
            "read/write reopen persists bytes"
        );

        let lock_path = storage_path.join("history.lock");
        let lock = open_or_create_secure_file(&lock_path).expect("create secure lock file");
        let lock_identity = storage_identity(
            lock.as_raw_handle() as HANDLE,
            StorageObjectKind::RegularFile,
        )
        .expect("lock identity verifies");
        drop(lock);
        let reopened_lock =
            open_or_create_secure_file(&lock_path).expect("open existing secure lock file");
        assert!(
            storage_identity(
                reopened_lock.as_raw_handle() as HANDLE,
                StorageObjectKind::RegularFile,
            )
            .expect("reopened lock identity verifies")
                == lock_identity,
            "open-or-create preserves identity"
        );

        drop(reopened_lock);
        drop(reopened);
        drop(directory);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn permissive_existing_file_is_rejected_without_mutation() {
        let root = TempRoot::create().expect("create temporary root");
        let storage_path = root.join("storage");
        let directory =
            ensure_secure_storage_directory(&storage_path).expect("create secure directory");
        let file_path = storage_path.join("legacy.json");
        let mut retained = create_permissive_test_file(&file_path, b"legacy bytes")
            .expect("create permissive file with a retained writer");
        let before = read_acl_snapshot(retained.as_raw_handle() as HANDLE)
            .expect("snapshot permissive file DACL");

        open_existing_secure_file(&file_path, false)
            .expect_err("a permissive existing file is never repaired in place");
        assert!(
            read_acl_snapshot(retained.as_raw_handle() as HANDLE).expect("read rejected file DACL")
                == before,
            "rejection leaves the permissive DACL unchanged"
        );

        retained
            .seek(SeekFrom::End(0))
            .expect("seek retained writer");
        retained
            .write_all(b"!")
            .expect("retained access survives any later DACL change");
        retained.sync_all().expect("sync retained writer");
        assert!(
            fs::read(&file_path).expect("read retained-writer bytes") == b"legacy bytes!",
            "test demonstrates why in-place DACL repair cannot establish a boundary"
        );

        drop(retained);
        drop(directory);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn permissive_existing_directory_is_rejected_without_mutation() {
        let root = TempRoot::create().expect("create temporary root");
        let storage_path = root.join("legacy-storage");
        let retained =
            create_permissive_test_directory(&storage_path).expect("create permissive directory");
        let before = read_acl_snapshot(retained.as_raw_handle() as HANDLE)
            .expect("snapshot permissive directory DACL");

        ensure_secure_storage_directory(&storage_path)
            .expect_err("a permissive existing directory is never repaired in place");
        assert!(
            read_acl_snapshot(retained.as_raw_handle() as HANDLE)
                .expect("read rejected directory DACL")
                == before,
            "rejection leaves the permissive directory DACL unchanged"
        );

        drop(retained);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn final_file_symlink_and_directory_junction_fail_without_touching_targets() {
        let root = TempRoot::create().expect("create temporary root");
        let storage_path = root.join("storage");
        let directory =
            ensure_secure_storage_directory(&storage_path).expect("create secure directory");

        let file_target_path = storage_path.join("file-target.json");
        let file_target = create_permissive_test_file(&file_target_path, b"target file bytes")
            .expect("create file target");
        let file_target_acl = read_acl_snapshot(file_target.as_raw_handle() as HANDLE)
            .expect("snapshot file target DACL");
        let file_link = storage_path.join("file-link.json");
        symlink_file(&file_target_path, &file_link)
            .expect("create unprivileged final file symlink");

        assert!(
            open_existing_secure_file(&file_link, false).is_err(),
            "final file symlink is rejected"
        );
        assert!(
            fs::read(&file_target_path).expect("read file target") == b"target file bytes",
            "file target bytes are unchanged"
        );
        assert!(
            read_acl_snapshot(file_target.as_raw_handle() as HANDLE)
                .expect("read file target DACL after rejection")
                == file_target_acl,
            "file target DACL is unchanged"
        );
        fs::remove_file(&file_link).expect("remove file symlink");

        let directory_target_path = storage_path.join("directory-target");
        let directory_target = create_permissive_test_directory(&directory_target_path)
            .expect("create directory target");
        let marker_path = directory_target_path.join("marker.bin");
        fs::write(&marker_path, b"target directory bytes").expect("write target marker");
        let directory_target_acl = read_acl_snapshot(directory_target.as_raw_handle() as HANDLE)
            .expect("snapshot directory target DACL");
        let junction_path = storage_path.join("directory-junction");
        create_junction(&junction_path, &directory_target_path)
            .expect("create unprivileged directory junction");

        assert!(
            ensure_secure_storage_directory(&junction_path).is_err(),
            "final directory junction is rejected"
        );
        assert!(
            fs::read(&marker_path).expect("read target marker") == b"target directory bytes",
            "directory target bytes are unchanged"
        );
        assert!(
            read_acl_snapshot(directory_target.as_raw_handle() as HANDLE)
                .expect("read directory target DACL after rejection")
                == directory_target_acl,
            "directory target DACL is unchanged"
        );
        fs::remove_dir(&junction_path).expect("remove directory junction");

        drop(directory_target);
        drop(file_target);
        drop(directory);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn ancestor_junction_resolves_to_real_final_objects_with_matching_identity() {
        let root = TempRoot::create().expect("create temporary root");
        let real_parent = root.join("real-parent");
        fs::create_dir(&real_parent).expect("create real parent");
        let junction_parent = root.join("junction-parent");
        create_junction(&junction_parent, &real_parent).expect("create ancestor junction");

        let storage_through_junction = junction_parent.join("storage");
        let storage_physical = real_parent.join("storage");
        let directory = ensure_secure_storage_directory(&storage_through_junction)
            .expect("create real final directory through ancestor junction");
        let physical_directory = open_windows_path(
            &storage_physical,
            FILE_READ_ATTRIBUTES | READ_CONTROL,
            OPEN_EXISTING,
            StorageObjectKind::Directory.open_flags(),
        )
        .expect("open physical directory target");
        assert!(
            storage_identity(
                directory.as_raw_handle() as HANDLE,
                StorageObjectKind::Directory,
            )
            .expect("read junction-path directory identity")
                == storage_identity(
                    physical_directory.as_raw_handle() as HANDLE,
                    StorageObjectKind::Directory,
                )
                .expect("read physical directory identity"),
            "junction and physical directory paths name the same final object"
        );

        let file_through_junction = storage_through_junction.join("history.json");
        let file_physical = storage_physical.join("history.json");
        let mut file = create_new_secure_file(&file_through_junction)
            .expect("create real final file through ancestor junction");
        file.write_all(b"junction bytes").expect("write file");
        file.sync_all().expect("sync file");
        let mut physical_file = open_windows_path(
            &file_physical,
            GENERIC_READ | FILE_READ_ATTRIBUTES | READ_CONTROL,
            OPEN_EXISTING,
            StorageObjectKind::RegularFile.open_flags(),
        )
        .expect("open physical file target");
        assert!(
            storage_identity(
                file.as_raw_handle() as HANDLE,
                StorageObjectKind::RegularFile
            )
            .expect("read junction-path file identity")
                == storage_identity(
                    physical_file.as_raw_handle() as HANDLE,
                    StorageObjectKind::RegularFile,
                )
                .expect("read physical file identity"),
            "junction and physical file paths name the same final object"
        );
        assert!(
            read_open_file(&mut physical_file).expect("read physical file") == b"junction bytes",
            "physical target contains bytes written through junction"
        );

        drop(physical_file);
        drop(file);
        drop(physical_directory);
        drop(directory);
        fs::remove_dir(&junction_parent).expect("remove ancestor junction");
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn validation_handle_blocks_replacement_until_identity_check_finishes() {
        let root = TempRoot::create().expect("create temporary root");
        let storage_path = root.join("storage");
        let directory =
            ensure_secure_storage_directory(&storage_path).expect("create secure directory");
        let live_path = storage_path.join("history.json");
        let replacement_path = storage_path.join("replacement.json");
        let detached_path = storage_path.join("detached.json");

        let original = create_new_secure_file(&live_path).expect("create original file");
        let original_identity = storage_identity(
            original.as_raw_handle() as HANDLE,
            StorageObjectKind::RegularFile,
        )
        .expect("read original identity");
        let replacement =
            create_new_secure_file(&replacement_path).expect("create replacement file");
        drop(replacement);

        verify_path_identity_with(
            &live_path,
            StorageObjectKind::RegularFile,
            original_identity,
            |_validation| {
                OpenOptions::new()
                    .access_mode(DELETE)
                    .share_mode(STORAGE_SHARE_MODE)
                    .open(&live_path)
                    .expect_err("validation handle blocks a competing DELETE-access open");
                let blocked = Command::new("cmd")
                    .arg("/D")
                    .arg("/C")
                    .arg("move")
                    .arg("/Y")
                    .arg(&live_path)
                    .arg(&detached_path)
                    .output()?;
                assert!(
                    !blocked.status.success(),
                    "another process cannot detach the path during validation"
                );
                assert!(
                    live_path.exists(),
                    "live path remains installed during validation"
                );
                assert!(
                    !detached_path.exists(),
                    "detached path is absent while validation handle is alive"
                );
                Ok(())
            },
        )
        .expect("point-in-time identity validation succeeds");

        let detached = Command::new("cmd")
            .arg("/D")
            .arg("/C")
            .arg("move")
            .arg("/Y")
            .arg(&live_path)
            .arg(&detached_path)
            .output()
            .expect("run detach rename after validation");
        assert!(
            detached.status.success(),
            "detach rename succeeds after validation handle closes"
        );
        fs::rename(&replacement_path, &live_path)
            .expect("replacement install succeeds after validation handle closes");

        drop(original);
        drop(directory);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn path_replacement_is_detected_without_rewriting_either_file() {
        let root = TempRoot::create().expect("create temporary root");
        let storage_path = root.join("storage");
        let directory =
            ensure_secure_storage_directory(&storage_path).expect("create secure directory");
        let live_path = storage_path.join("history.json");
        let replacement_path = storage_path.join("replacement.json");
        let detached_path = storage_path.join("detached.json");

        let mut original = create_new_secure_file(&live_path).expect("create original file");
        original
            .write_all(b"old open handle bytes")
            .expect("write original file");
        original.sync_all().expect("sync original file");
        let original_identity = storage_identity(
            original.as_raw_handle() as HANDLE,
            StorageObjectKind::RegularFile,
        )
        .expect("read original identity");

        let mut replacement =
            create_new_secure_file(&replacement_path).expect("create replacement file");
        replacement
            .write_all(b"new path bytes")
            .expect("write replacement file");
        replacement.sync_all().expect("sync replacement file");
        let replacement_identity = storage_identity(
            replacement.as_raw_handle() as HANDLE,
            StorageObjectKind::RegularFile,
        )
        .expect("read replacement identity");
        assert!(
            original_identity != replacement_identity,
            "test files have distinct identities"
        );
        drop(replacement);

        fs::rename(&live_path, &detached_path).expect("detach original path");
        fs::rename(&replacement_path, &live_path).expect("install replacement path");
        assert!(
            verify_secure_file_path(&original, &live_path).is_err(),
            "identity revalidation detects replacement"
        );
        assert!(
            read_open_file(&mut original).expect("read old open handle")
                == b"old open handle bytes",
            "old open handle bytes are unchanged"
        );
        assert!(
            fs::read(&live_path).expect("read replacement path") == b"new path bytes",
            "replacement path bytes are unchanged"
        );

        drop(original);
        drop(directory);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn file_and_directory_helpers_reject_the_wrong_object_type() {
        let root = TempRoot::create().expect("create temporary root");
        let directory_path = root.join("permissive-directory");
        let directory =
            create_permissive_test_directory(&directory_path).expect("create permissive directory");
        let directory_acl = read_acl_snapshot(directory.as_raw_handle() as HANDLE)
            .expect("snapshot permissive directory DACL");
        assert!(
            open_existing_secure_file(&directory_path, false).is_err(),
            "file helper rejects a directory"
        );
        assert!(
            read_acl_snapshot(directory.as_raw_handle() as HANDLE)
                .expect("read rejected directory DACL")
                == directory_acl,
            "wrong-type rejection leaves the original permissive directory DACL unchanged"
        );

        let regular_path = root.join("permissive-regular-file");
        let regular = create_permissive_test_file(&regular_path, b"regular bytes")
            .expect("create permissive regular file");
        let regular_acl = read_acl_snapshot(regular.as_raw_handle() as HANDLE)
            .expect("snapshot permissive regular file DACL");
        assert!(
            ensure_secure_storage_directory(&regular_path).is_err(),
            "directory helper rejects a regular file"
        );
        assert!(
            read_acl_snapshot(regular.as_raw_handle() as HANDLE)
                .expect("read rejected regular file DACL")
                == regular_acl,
            "wrong-type rejection leaves the original permissive file DACL unchanged"
        );

        drop(regular);
        drop(directory);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn security_errors_do_not_disclose_path_sid_or_username() {
        let root = TempRoot::create().expect("create temporary root");
        let sensitive_path = root.join("private-storage-object");
        let file = create_permissive_test_file(&sensitive_path, b"private")
            .expect("create sensitive test file");
        let path_text = sensitive_path.to_string_lossy().into_owned();
        let current_user = current_process_user_sid().expect("read current process SID");
        let sid_text = sid_string(&current_user).expect("format current process SID");
        let username = std::env::var("USERNAME").expect("read current Windows username");

        let path_error = ensure_secure_storage_directory(&sensitive_path)
            .expect_err("wrong-type path is rejected generically");
        assert_generic_error(&path_error, &[&path_text, &sid_text, &username]);

        let local_system = well_known_sid(WinLocalSystemSid).expect("create LocalSystem SID");
        let everyone = well_known_sid(WinWorldSid).expect("create Everyone SID");
        let foreign_owner = if current_user.as_bytes() != local_system.as_bytes() {
            &local_system
        } else {
            &everyone
        };
        let owner_error = inspect_owner(foreign_owner.as_bytes(), current_user.as_bytes())
            .expect_err("foreign owner is rejected generically");
        assert_generic_error(&owner_error, &[&path_text, &sid_text, &username]);

        drop(file);
        root.cleanup().expect("remove temporary root");
    }

    #[test]
    fn directory_flush_and_missing_inputs_fail_closed() {
        let root = TempRoot::create().expect("create temporary root");
        let storage_path = root.join("storage");
        let directory =
            ensure_secure_storage_directory(&storage_path).expect("create secure directory");
        flush_secure_storage_directory(&directory).expect("valid directory flush succeeds");
        assert!(
            flush_storage_directory_handle(INVALID_HANDLE_VALUE).is_err(),
            "invalid handle is rejected"
        );

        let missing_file = storage_path.join("missing.json");
        assert!(
            open_existing_secure_file(&missing_file, false).is_err(),
            "nonexistent file is rejected"
        );
        let missing_parent = root.join("missing-parent");
        assert!(
            ensure_secure_storage_directory(&missing_parent.join("storage")).is_err(),
            "missing ancestor is not created"
        );
        assert!(!missing_parent.exists(), "missing ancestor remains absent");

        drop(directory);
        root.cleanup().expect("remove temporary root");
    }
}
