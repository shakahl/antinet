﻿/*
 * Anti managed profiler code. Written by de4dot@gmail.com
 * This code is in the public domain.
 * Official site: https://bitbucket.org/0xd4d/antinet
 */

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace antinet {
	/// <summary>
	/// This class will make sure that no managed .NET profiler is working.
	/// </summary>
	/// <remarks>
	/// <para>
	/// To detect profilers that are loaded when the CLR is loaded, this code will find
	/// the CLR profiler status flag in the data section. If CLR 4.0 is used, the code
	/// will find instructions in clr.dll that compares a dword location with the value 4.
	/// 4 is the value that is stored when a profiler has successfully attached to the
	/// CLR. If CLR 2.0 is used, then it will look for code that tests bits 1 and 2 of
	/// some dword location.
	/// </para>
	/// <para>
	/// CLR 4.0 allows a profiler to attach at any time. For this to work, it will create
	/// a named event, called "Global\CPFATE_PID_vCLRVERSION" where PID is the pid
	/// of the process the CLR is in and CLRVERSION is the first 3 version numbers
	/// (eg. 4.0.30319). It's actually the Finalizer thread that waits on this event. :)
	/// </para>
	/// <para>
	/// When a profiler tries to attach, it will try to connect to a named pipe. This pipe's
	/// name is called "\\.\pipe\CPFATP_PID_vCLRVERSION". It will then signal the above event to
	/// wake up the Finalizer thread. If the event can't be created, then no profiler can ever
	/// attach. Any code that runs before the CLR has a chance to "steal" this event from it
	/// to prevent the CLR from allowing profilers to attach at runtime. We can't do it. But
	/// we can create the named pipe. If we own the named pipe, then no profiler can ever send
	/// the attach message and they'll never be able to attach.
	/// </para>
	/// <para>
	/// Most of the time, the named pipe isn't created. All we do is create the named pipe
	/// and we've prevented profilers from attaching at runtime. If the pipe has already been
	/// created, we must make sure the CLR closes the pipe and exits the "profiler attacher"
	/// thread. By default, it will wait up to 5 mins (300,000ms) before exiting the wait loop.
	/// You can change this value with the ProfAPIMaxWaitForTriggerMs option (dword in registry)
	/// or COMPlus_ProfAPIMaxWaitForTriggerMs environment value. If the AttachThreadAlwaysOn
	/// option (COMPlus_AttachThreadAlwaysOn env value) is enabled, the attach thread will
	/// never exit and the named pipe is never closed. It's possible to close the thread and
	/// the named pipe, but it requires more memory patching. See the code for details.
	/// </para>
	/// </remarks>
	public static class AntiManagedProfiler {
		static IProfilerDetector profilerDetector;

		interface IProfilerDetector {
			bool IsProfilerAttached { get; }
			bool WasProfilerAttached { get; }
			bool Initialize();
			void PreventActiveProfilerFromReceivingProfilingMessages();
		}

		class ProfilerDetectorCLR20 : IProfilerDetector {
			/// <summary>
			/// Address of CLR 2.0's profiler status flag. If one or both of bits 1 or 2 is set,
			/// a profiler is attached.
			/// </summary>
			IntPtr profilerStatusFlag;

			bool wasAttached;

			public bool IsProfilerAttached {
				get {
					unsafe {
						if (profilerStatusFlag == IntPtr.Zero)
							return false;
						return (*(uint*)profilerStatusFlag & 6) != 0;
					}
				}
			}

			public bool WasProfilerAttached {
				get { return wasAttached; }
			}

			public bool Initialize() {
				bool result = FindProfilerStatus();
				wasAttached = IsProfilerAttached;
				return result;
			}

			/// <summary>
			/// This code tries to find the CLR 2.0 profiler status flag. It searches the whole
			/// .text section for a certain instruction.
			/// </summary>
			/// <returns><c>true</c> if it was found, <c>false</c> otherwise</returns>
			unsafe bool FindProfilerStatus() {
				// Record each hit here and pick the one with the most hits
				var addrCounts = new Dictionary<IntPtr, int>();
				try {
					var peInfo = PEInfo.GetCLR();
					if (peInfo == null)
						return false;

					IntPtr sectionAddr;
					uint sectionSize;
					if (!peInfo.FindSection(".text", out sectionAddr, out sectionSize))
						return false;

					const int MAX_COUNTS = 50;
					byte* p = (byte*)sectionAddr;
					byte* end = (byte*)sectionAddr + sectionSize;
					for (; p < end; p++) {
						IntPtr addr;

						// F6 05 XX XX XX XX 06	test byte ptr [mem],6
						if (*p == 0xF6 && p[1] == 0x05 && p[6] == 0x06) {
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 2));
							else
								addr = new IntPtr((void*)(p + 7 + *(int*)(p + 2)));
						}
						else
							continue;

						if (!PEInfo.IsAligned(addr, 4))
							continue;
						if (!peInfo.IsValidImageAddress(addr, 4))
							continue;

						try {
							*(uint*)addr = *(uint*)addr;
						}
						catch {
							continue;
						}

						int count = 0;
						addrCounts.TryGetValue(addr, out count);
						count++;
						addrCounts[addr] = count;
						if (count >= MAX_COUNTS)
							break;
					}
				}
				catch {
				}
				var foundAddr = GetMax(addrCounts, 5);
				if (foundAddr == IntPtr.Zero)
					return false;

				profilerStatusFlag = foundAddr;
				return true;
			}

			public unsafe void PreventActiveProfilerFromReceivingProfilingMessages() {
				if (profilerStatusFlag == IntPtr.Zero)
					return;
				*(uint*)profilerStatusFlag &= ~6U;
			}
		}

		class ProfilerDetectorCLR40 : IProfilerDetector {
			const uint PIPE_ACCESS_DUPLEX = 3;
			const uint PIPE_TYPE_MESSAGE = 4;
			const uint PIPE_READMODE_MESSAGE = 2;
			const uint FILE_FLAG_OVERLAPPED = 0x40000000;
			const uint GENERIC_READ = 0x80000000;
			const uint GENERIC_WRITE = 0x40000000;
			const uint OPEN_EXISTING = 3;
			const uint PAGE_EXECUTE_READWRITE = 0x40;

			[DllImport("kernel32", CharSet = CharSet.Auto)]
			static extern uint GetCurrentProcessId();

			[DllImport("kernel32", CharSet = CharSet.Auto)]
			static extern void Sleep(uint dwMilliseconds);

			[DllImport("kernel32", SetLastError = true)]
			static extern SafeFileHandle CreateNamedPipe(string lpName, uint dwOpenMode,
			   uint dwPipeMode, uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize,
			   uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

			[DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
			static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
			   uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
			   uint dwFlagsAndAttributes, IntPtr hTemplateFile);

			[DllImport("kernel32")]
			static extern bool VirtualProtect(IntPtr lpAddress, int dwSize, uint flNewProtect, out uint lpflOldProtect);

			const uint ConfigDWORDInfo_name = 0;
			static readonly uint ConfigDWORDInfo_defValue = (uint)IntPtr.Size;
			const string ProfAPIMaxWaitForTriggerMs_name = "ProfAPIMaxWaitForTriggerMs";

			/// <summary>
			/// Address of the profiler control block. Only some fields are interesting and
			/// here they are in order:
			/// 
			/// <code>
			/// EEToProfInterfaceImpl*
			/// uint profilerEventMask
			/// uint profilerStatus
			/// </code>
			/// 
			/// <c>profilerStatus</c> is <c>0</c> when no profiler is attacheded. Any other value
			/// indicates that a profiler is attached, attaching, or detaching. It's <c>4</c>
			/// when a profiler is attached. When it's attached, it will receive messages from
			/// the CLR.
			/// </summary>
			IntPtr profilerControlBlock;

			SafeFileHandle profilerPipe;

			bool wasAttached;

			public bool IsProfilerAttached {
				get {
					unsafe {
						if (profilerControlBlock == IntPtr.Zero)
							return false;
						return *(uint*)((byte*)profilerControlBlock + IntPtr.Size + 4) != 0;
					}
				}
			}

			public bool WasProfilerAttached {
				get { return wasAttached; }
			}

			public bool Initialize() {
				bool result = FindProfilerControlBlock();
				result &= TakeOwnershipOfNamedPipe() || CreateNamedPipe();
				wasAttached = IsProfilerAttached;
				return result;
			}

			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			unsafe bool TakeOwnershipOfNamedPipe() {
				try {
					if (CreateNamedPipe())
						return true;

					// The CLR has already created the named pipe. Either the AttachThreadAlwaysOn
					// CLR option is enabled or some profiler has just attached or is attaching.
					// We must force it to exit its loop. There are two options that can prevent
					// it from exiting the thread, AttachThreadAlwaysOn and
					// ProfAPIMaxWaitForTriggerMs. If AttachThreadAlwaysOn is enabled, the thread
					// is started immediately when the CLR is loaded and it never exits.
					// ProfAPIMaxWaitForTriggerMs is the timeout in ms to use when waiting on
					// client attach messages. A user could set this to FFFFFFFF which is equal
					// to the INFINITE constant.
					//
					// To force it to exit, we must do this:
					//	- Find clr!ProfilingAPIAttachDetach::s_attachThreadingMode and make sure
					//	  it's not 2 (AttachThreadAlwaysOn is enabled).
					//	- Find clr!EXTERNAL_ProfAPIMaxWaitForTriggerMs and:
					//		- Set its default value to 0
					//		- Rename the option so the user can't override it
					//	- Open the named pipe to wake it up and then close the file to force a
					//	  timeout error.
					//	- Wait a little while until the thread has exited

					IntPtr threadingModeAddr = FindThreadingModeAddress();
					IntPtr timeOutOptionAddr = FindTimeOutOptionAddress();

					if (timeOutOptionAddr == IntPtr.Zero)
						return false;

					// Make sure the thread can exit. If this value is 2, it will never exit.
					if (threadingModeAddr != IntPtr.Zero && *(uint*)threadingModeAddr == 2)
						*(uint*)threadingModeAddr = 1;

					// Set default timeout to 0 and rename timeout option
					FixTimeOutOption(timeOutOptionAddr);

					// Wake up clr!ProfilingAPIAttachServer::ConnectToClient(). We immediately
					// close the pipe so it will fail to read any data. It will then start over
					// again but this time, its timeout value will be 0, and it will fail. Since
					// the thread can now exit, it will exit and close its named pipe.
					using (var hPipe = CreatePipeFileHandleWait()) {
						if (hPipe == null)
							return false;
						if (hPipe.IsInvalid)
							return false;
					}

					return CreateNamedPipeWait();
				}
				catch {
				}
				return false;
			}

			bool CreateNamedPipeWait() {
				int timeLeft = 100;
				const int waitTime = 5;
				while (timeLeft > 0) {
					if (CreateNamedPipe())
						return true;
					Sleep(waitTime);
					timeLeft -= waitTime;
				}
				return CreateNamedPipe();
			}

			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			unsafe static void FixTimeOutOption(IntPtr timeOutOptionAddr) {
				if (timeOutOptionAddr == IntPtr.Zero)
					return;

				uint oldProtect;
				VirtualProtect(timeOutOptionAddr, (int)ConfigDWORDInfo_defValue + 4, PAGE_EXECUTE_READWRITE, out oldProtect);
				try {
					// Set default timeout to 0 to make sure it fails immediately
					*(uint*)((byte*)timeOutOptionAddr + ConfigDWORDInfo_defValue) = 0;

				}
				finally {
					VirtualProtect(timeOutOptionAddr, IntPtr.Size, oldProtect, out oldProtect);
				}

				// Rename the option to make sure the user can't override the value
				char* name = *(char**)((byte*)timeOutOptionAddr + ConfigDWORDInfo_name);
				VirtualProtect(new IntPtr(name), ProfAPIMaxWaitForTriggerMs_name.Length * 2, PAGE_EXECUTE_READWRITE, out oldProtect);
				try {
					var rand = new Random();
					while (*name != 0) {
						*name = (char)rand.Next(1, ushort.MaxValue);
						name++;
					}
				}
				finally {
					VirtualProtect(timeOutOptionAddr, IntPtr.Size, oldProtect, out oldProtect);
				}
			}

			SafeFileHandle CreatePipeFileHandleWait() {
				int timeLeft = 100;
				const int waitTime = 5;
				while (timeLeft > 0) {
					if (CreateNamedPipe())
						return null;
					var hFile = CreatePipeFileHandle();
					if (!hFile.IsInvalid)
						return hFile;
					Sleep(waitTime);
					timeLeft -= waitTime;
				}
				return CreatePipeFileHandle();
			}

			static SafeFileHandle CreatePipeFileHandle() {
				return CreateFile(GetPipeName(), GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
			}

			static string GetPipeName() {
				return string.Format(@"\\.\pipe\CPFATP_{0}_v{1}.{2}.{3}",
							GetCurrentProcessId(), Environment.Version.Major,
							Environment.Version.Minor, Environment.Version.Build);
			}

			bool CreateNamedPipe() {
				if (profilerPipe != null && !profilerPipe.IsInvalid)
					return true;

				profilerPipe = CreateNamedPipe(GetPipeName(),
											FILE_FLAG_OVERLAPPED | PIPE_ACCESS_DUPLEX,
											PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE,
											1,			// nMaxInstances
											0x24,		// nOutBufferSize
											0x338,		// nInBufferSize
											1000,		// nDefaultTimeOut
											IntPtr.Zero);	// lpSecurityAttributes

				return !profilerPipe.IsInvalid;
			}

			/// <summary>
			/// Finds the address of clr!s_attachThreadingMode
			/// </summary>
			/// <returns>The address or <c>null</c> if none was found</returns>
			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			static unsafe IntPtr FindThreadingModeAddress() {
				try {
					// Find this code in clr!ProfilingAPIAttachServer::ExecutePipeRequests()
					//	83 3D XX XX XX XX 02	cmp dword ptr [mem],2
					//	74 / 0F 84 XX			je there
					//	83 E8+r 00 / 85 C0+rr	sub reg,0 / test reg,reg
					//	74 / 0F 84 XX			je there
					//	48+r / FF C8+r			dec reg
					//	74 / 0F 84 XX			je there
					//	48+r / FF C8+r			dec reg

					var peInfo = PEInfo.GetCLR();
					if (peInfo == null)
						return IntPtr.Zero;

					IntPtr sectionAddr;
					uint sectionSize;
					if (!peInfo.FindSection(".text", out sectionAddr, out sectionSize))
						return IntPtr.Zero;

					byte* ptr = (byte*)sectionAddr;
					byte* end = (byte*)sectionAddr + sectionSize;
					for (; ptr < end; ptr++) {
						IntPtr addr;

						try {
							//	83 3D XX XX XX XX 02	cmp dword ptr [mem],2
							byte* p = ptr;
							if (*p != 0x83 || p[1] != 0x3D || p[6] != 2)
								continue;
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 2));
							else
								addr = new IntPtr((void*)(p + 7 + *(int*)(p + 2)));
							if (!PEInfo.IsAligned(addr, 4))
								continue;
							if (!peInfo.IsValidImageAddress(addr))
								continue;
							p += 7;

							// 1 = normal lazy thread creation. 2 = thread is always present
							if (*(uint*)addr < 1 || *(uint*)addr > 2)
								continue;
							*(uint*)addr = *(uint*)addr;

							//	74 / 0F 84 XX			je there
							if (!NextJz(ref p))
								continue;

							//	83 E8+r 00 / 85 C0+rr	sub reg,0 / test reg,reg
							SkipRex(ref p);
							if (*p == 0x83 && p[2] == 0) {
								if ((uint)(p[1] - 0xE8) > 7)
									continue;
								p += 3;
							}
							else if (*p == 0x85) {
								int reg = (p[1] >> 3) & 7;
								int rm = p[1] & 7;
								if (reg != rm)
									continue;
								p += 2;
							}
							else
								continue;

							//	74 / 0F 84 XX			je there
							if (!NextJz(ref p))
								continue;

							//	48+r / FF C8+r			dec reg
							if (!SkipDecReg(ref p))
								continue;

							//	74 / 0F 84 XX			je there
							if (!NextJz(ref p))
								continue;

							//	48+r / FF C8+r			dec reg
							if (!SkipDecReg(ref p))
								continue;

							return addr;
						}
						catch {
						}
					}
				}
				catch {
				}
				return IntPtr.Zero;
			}

			/// <summary>
			/// Finds the address of clr!EXTERNAL_ProfAPIMaxWaitForTriggerMs
			/// </summary>
			/// <returns>The address or <c>null</c> if none was found</returns>
			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			static unsafe IntPtr FindTimeOutOptionAddress() {
				try {
					var peInfo = PEInfo.GetCLR();
					if (peInfo == null)
						return IntPtr.Zero;

					IntPtr sectionAddr;
					uint sectionSize;
					if (!peInfo.FindSection(".rdata", out sectionAddr, out sectionSize) &&
						!peInfo.FindSection(".text", out sectionAddr, out sectionSize))
						return IntPtr.Zero;

					byte* p = (byte*)sectionAddr;
					byte* end = (byte*)sectionAddr + sectionSize;
					for (; p < end; p++) {
						try {
							char* name = *(char**)(p + ConfigDWORDInfo_name);
							if (!PEInfo.IsAligned(new IntPtr(name), 2))
								continue;
							if (!peInfo.IsValidImageAddress(name))
								continue;

							if (!Equals(name, ProfAPIMaxWaitForTriggerMs_name))
								continue;

							return new IntPtr(p);
						}
						catch {
						}
					}
				}
				catch {
				}
				return IntPtr.Zero;
			}

			unsafe static bool Equals(char* s1, string s2) {
				for (int i = 0; i < s2.Length; i++) {
					if (char.ToUpperInvariant(s1[i]) != char.ToUpperInvariant(s2[i]))
						return false;
				}
				return s1[s2.Length] == 0;
			}

			unsafe static void SkipRex(ref byte* p) {
				if (IntPtr.Size != 8)
					return;
				if (*p >= 0x48 && *p <= 0x4F)
					p++;
			}

			unsafe static bool SkipDecReg(ref byte* p) {
				SkipRex(ref p);
				if (IntPtr.Size == 4 && *p >= 0x48 && *p <= 0x4F)
					p++;
				else if (*p == 0xFF && p[1] >= 0xC8 && p[1] <= 0xCF)
					p += 2;
				else
					return false;
				return true;
			}

			unsafe static bool NextJz(ref byte* p) {
				if (*p == 0x74) {
					p += 2;
					return true;
				}

				if (*p == 0x0F && p[1] == 0x84) {
					p += 6;
					return true;
				}

				return false;
			}

			/// <summary>
			/// This code tries to find the CLR 4.0 profiler control block address. It does this
			/// by searching for the code that accesses the profiler status field.
			/// </summary>
			/// <returns><c>true</c> if it was found, <c>false</c> otherwise</returns>
			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			unsafe bool FindProfilerControlBlock() {
				// Record each hit here and pick the one with the most hits
				var addrCounts = new Dictionary<IntPtr, int>();
				try {
					var peInfo = PEInfo.GetCLR();
					if (peInfo == null)
						return false;

					IntPtr sectionAddr;
					uint sectionSize;
					if (!peInfo.FindSection(".text", out sectionAddr, out sectionSize))
						return false;

					const int MAX_COUNTS = 50;
					byte* p = (byte*)sectionAddr;
					byte* end = (byte*)sectionAddr + sectionSize;
					for (; p < end; p++) {
						IntPtr addr;

						// A1 xx xx xx xx		mov eax,[mem]
						// 83 F8 04				cmp eax,4
						if (*p == 0xA1 && p[5] == 0x83 && p[6] == 0xF8 && p[7] == 0x04) {
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 1));
							else
								addr = new IntPtr((void*)(p + 5 + *(int*)(p + 1)));
						}
						// 8B 05 xx xx xx xx	mov eax,[mem]
						// 83 F8 04				cmp eax,4
						else if (*p == 0x8B && p[1] == 0x05 && p[6] == 0x83 && p[7] == 0xF8 && p[8] == 0x04) {
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 2));
							else
								addr = new IntPtr((void*)(p + 6 + *(int*)(p + 2)));
						}
						// 83 3D XX XX XX XX 04	cmp dword ptr [mem],4
						else if (*p == 0x83 && p[1] == 0x3D && p[6] == 0x04) {
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 2));
							else
								addr = new IntPtr((void*)(p + 7 + *(int*)(p + 2)));
						}
						else
							continue;

						if (!PEInfo.IsAligned(addr, 4))
							continue;
						if (!peInfo.IsValidImageAddress(addr, 4))
							continue;

						// Valid values are 0-4. 4 being attached.
						try {
							if (*(uint*)addr > 4)
								continue;
							*(uint*)addr = *(uint*)addr;
						}
						catch {
							continue;
						}

						int count = 0;
						addrCounts.TryGetValue(addr, out count);
						count++;
						addrCounts[addr] = count;
						if (count >= MAX_COUNTS)
							break;
					}
				}
				catch {
				}
				var foundAddr = GetMax(addrCounts, 5);
				if (foundAddr == IntPtr.Zero)
					return false;

				profilerControlBlock = new IntPtr((byte*)foundAddr - (IntPtr.Size + 4));
				return true;
			}

			public unsafe void PreventActiveProfilerFromReceivingProfilingMessages() {
				if (profilerControlBlock == IntPtr.Zero)
					return;
				*(uint*)((byte*)profilerControlBlock + IntPtr.Size + 4) = 0;
			}
		}

		/// <summary>
		/// Returns <c>true</c> if a profiler was attached, is attaching or detaching.
		/// </summary>
		public static bool IsProfilerAttached {
			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			get {
				try {
					if (profilerDetector == null)
						return false;
					return profilerDetector.IsProfilerAttached;
				}
				catch {
				}
				return false;
			}
		}

		/// <summary>
		/// Returns <c>true</c> if a profiler was attached, is attaching or detaching.
		/// </summary>
		public static bool WasProfilerAttached {
			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			get {
				try {
					if (profilerDetector == null)
						return false;
					return profilerDetector.WasProfilerAttached;
				}
				catch {
				}
				return false;
			}
		}

		/// <summary>
		/// Must be called to initialize anti-managed profiler code. This method should only
		/// be called once per process. I.e., don't call it from every loaded .NET DLL.
		/// </summary>
		/// <returns><c>true</c> if successful, <c>false</c> otherwise</returns>
		public static bool Initialize() {
			profilerDetector = CreateProfilerDetector();
			return profilerDetector.Initialize();
		}

		static IProfilerDetector CreateProfilerDetector() {
			if (Environment.Version.Major == 2)
				return new ProfilerDetectorCLR20();
			return new ProfilerDetectorCLR40();
		}

		/// <summary>
		/// Prevents any active profiler from receiving any profiling messages. Since the
		/// profiler is still in memory, it can call into the CLR even if it doesn't receive
		/// any messages. It's better to terminate the application than call this method.
		/// </summary>
		public static void PreventActiveProfilerFromReceivingProfilingMessages() {
			if (profilerDetector == null)
				return;
			profilerDetector.PreventActiveProfilerFromReceivingProfilingMessages();
		}

		static IntPtr GetMax(Dictionary<IntPtr, int> addresses, int minCount) {
			IntPtr foundAddr = IntPtr.Zero;
			int maxCount = 0;

			foreach (var kv in addresses) {
				if (foundAddr == IntPtr.Zero || maxCount < kv.Value) {
					foundAddr = kv.Key;
					maxCount = kv.Value;
				}
			}

			return maxCount >= minCount ? foundAddr : IntPtr.Zero;
		}
	}
}