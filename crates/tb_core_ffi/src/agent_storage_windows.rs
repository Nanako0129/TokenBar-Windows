//! Windows-only secure storage primitives shared by account and history stores.

use std::ffi::c_void;
use std::io;
use std::mem::size_of;
use std::ptr::{self, null, null_mut};
use std::slice;

use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, LocalFree, ERROR_INSUFFICIENT_BUFFER, ERROR_SUCCESS, HANDLE,
    INVALID_HANDLE_VALUE, NTSTATUS,
};
use windows_sys::Win32::Security::Authorization::{
    GetSecurityInfo, SetSecurityInfo, SE_FILE_OBJECT,
};
use windows_sys::Win32::Security::Cryptography::{
    BCryptGenRandom, BCRYPT_USE_SYSTEM_PREFERRED_RNG,
};
use windows_sys::Win32::Security::{
    AclSizeInformation, AddAccessAllowedAceEx, CopySid, CreateWellKnownSid, GetAce,
    GetAclInformation, GetLengthSid, GetSecurityDescriptorControl, GetSecurityDescriptorDacl,
    GetTokenInformation, InitializeAcl, IsValidAcl, IsValidSid, TokenUser, WinLocalSystemSid,
    ACCESS_ALLOWED_ACE, ACE_HEADER, ACL, ACL_REVISION, ACL_SIZE_INFORMATION,
    DACL_SECURITY_INFORMATION, NO_INHERITANCE, PROTECTED_DACL_SECURITY_INFORMATION,
    PSECURITY_DESCRIPTOR, PSID, SE_DACL_PROTECTED, TOKEN_QUERY, TOKEN_USER, WELL_KNOWN_SID_TYPE,
};
use windows_sys::Win32::Storage::FileSystem::FILE_ALL_ACCESS;
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

const ACCESS_ALLOWED_ACE_TYPE_VALUE: u8 = 0;
const SID_HEADER_LEN: usize = 8;
const ACE_SID_OFFSET: usize = size_of::<ACE_HEADER>() + size_of::<u32>();

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

/// Replace a file or directory handle's DACL with exactly current-user and
/// LocalSystem full-control ACEs, protect it from inheritance, then read it
/// back from the same handle and verify the complete ACL contract.
#[allow(dead_code)] // Stage 2A1 primitive; production callers are wired later.
pub(crate) fn protect_storage_handle(handle: HANDLE) -> io::Result<()> {
    validate_handle(handle)?;
    let current_user = current_process_user_sid()?;
    let local_system = well_known_sid(WinLocalSystemSid)?;
    let principals = expected_principals(&current_user, &local_system);
    let acl = build_full_control_acl(&principals)?;

    set_protected_handle_dacl(handle, acl.as_ptr())?;
    verify_storage_handle_with(handle, &current_user, &local_system)
}

/// Verify without modifying a file or directory handle's DACL.
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
    let snapshot = read_acl_snapshot(handle)?;
    inspect_acl(&snapshot, current_user.as_bytes(), local_system.as_bytes())
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

#[derive(Clone)]
struct AclSnapshot {
    dacl_present: bool,
    dacl_null: bool,
    protected: bool,
    aces: Vec<AceSnapshot>,
}

#[derive(Clone)]
struct AceSnapshot {
    ace_type: u8,
    flags: u8,
    mask: u32,
    sid: Vec<u8>,
}

fn read_acl_snapshot(handle: HANDLE) -> io::Result<AclSnapshot> {
    validate_handle(handle)?;

    let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
    let status = unsafe {
        GetSecurityInfo(
            handle,
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION,
            null_mut(),
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
        return Ok(snapshot);
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

    let acl_header = unsafe { ptr::read_unaligned(dacl) };
    let acl_size = acl_header.AclSize as usize;
    let bytes_in_use = info.AclBytesInUse as usize;
    if acl_size < size_of::<ACL>() || bytes_in_use < size_of::<ACL>() || bytes_in_use > acl_size {
        return Err(security_verification_failed());
    }
    let acl_start = dacl as usize;
    let acl_end = acl_start
        .checked_add(bytes_in_use)
        .ok_or_else(security_verification_failed)?;
    let first_ace = acl_start
        .checked_add(size_of::<ACL>())
        .ok_or_else(security_verification_failed)?;

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

    Ok(snapshot)
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
    use std::mem;
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    use windows_sys::Win32::Foundation::{GENERIC_READ, GENERIC_WRITE};
    use windows_sys::Win32::Security::{WinAuthenticatedUserSid, WinWorldSid, INHERITED_ACE};
    use windows_sys::Win32::Storage::FileSystem::{READ_CONTROL, WRITE_DAC};

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
                let result = OpenOptions::new()
                    .read(true)
                    .write(true)
                    .create_new(true)
                    .access_mode(GENERIC_READ | GENERIC_WRITE | READ_CONTROL | WRITE_DAC)
                    .open(&path);
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
    fn protected_dacl_round_trips_exact_principals_and_permissions() {
        let artifact = TempArtifact::create().expect("create temporary file");
        protect_storage_handle(artifact.handle()).expect("protect temporary file");

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
    fn broad_aces_fail_closed_and_are_removed_by_protection() {
        let artifact = TempArtifact::create().expect("create temporary file");

        for kind in [WinWorldSid, WinAuthenticatedUserSid] {
            let broad_sid = well_known_sid(kind).expect("create broad well-known SID");
            let broad_acl =
                build_full_control_acl(&[&broad_sid]).expect("build permissive temporary ACL");
            set_protected_handle_dacl(artifact.handle(), broad_acl.as_ptr())
                .expect("apply permissive temporary ACL");
            assert!(
                verify_storage_handle(artifact.handle()).is_err(),
                "broad ACE is rejected"
            );

            protect_storage_handle(artifact.handle()).expect("replace permissive ACL");
            verify_storage_handle(artifact.handle()).expect("replacement ACL verifies");
            let snapshot = read_acl_snapshot(artifact.handle()).expect("read replacement DACL");
            assert!(
                snapshot
                    .aces
                    .iter()
                    .all(|ace| ace.sid != broad_sid.as_bytes()),
                "broad ACE is absent after protection"
            );
        }

        artifact.cleanup().expect("remove temporary file");
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
}
