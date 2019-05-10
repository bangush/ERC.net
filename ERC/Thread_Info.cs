﻿using ERC.Structures;
using ERC_Lib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ERC
{
    public class ThreadInfo
    {
        #region Variables
        public IntPtr ThreadHandle { get; private set; }
        public int ThreadID { get; private set; }
        public CONTEXT32 Context32;
        public CONTEXT64 Context64;

        internal bool ThreadFailed { get; private set; }

        private bool X64 { get; set; }
        private ProcessThread ThreadCurrent { get; set; }
        private ProcessInfo ThreadProcess { get; set; }
        private ErcCore ThreadCore { get; set; }
        private ThreadBasicInformation ThreadBasicInfo = new ThreadBasicInformation();
        private TEB Teb;
        private List<byte[]> SehChain;
        #endregion

        #region Constructor
        internal ThreadInfo(ProcessThread thread, ErcCore core, ProcessInfo process)
        {
            ThreadID = thread.Id;
            ThreadCurrent = thread;
            ThreadCore = core;
            ThreadProcess = process;

            if (process.ProcessMachineType == MachineType.x64)
            {
                X64 = true;
            }

            try
            {
                ThreadHandle = ErcCore.OpenThread(ThreadAccess.All_ACCESS, false, (uint)thread.Id);
                if(ThreadHandle == null)
                {
                    ThreadFailed = true;
                    
                    throw new ERCException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
            }
            catch(ERCException e)
            {
                ErcResult<Exception> exceptionThrower = new ErcResult<Exception>(ThreadCore)
                {
                    Error = e
                };
                exceptionThrower.LogEvent();
            }

            PopulateTEB();
        }
        #endregion

        #region Get Thread Context
        /// <summary>
        /// Gets the register values of a thread and populates the CONTEXT structs. Should only be used on a suspended thread, results on an active thread are unreliable.
        /// </summary>
        /// <returns>Returns an ErcResult, the return value can be ignored, the object should only be checked for error values</returns>
        public ErcResult<string> Get_Context()
        {
            ErcResult<string> result = new ErcResult<string>(ThreadCore);
            
            if(X64 == true)
            {
                Context64 = new CONTEXT64();
                Context64.ContextFlags = CONTEXT_FLAGS.CONTEXT_ALL;
                try
                {
                    ErcCore.GetThreadContext64(ThreadHandle, ref Context64);
                    if(new Win32Exception(Marshal.GetLastWin32Error()).Message != "The operation completed successfully")
                    {
                        throw new ERCException("Win32 Exception encountered when attempting to get thread context" + 
                            new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }
                }
                catch (ERCException e)
                {
                    result.Error = e;
                    result.LogEvent();
                    return result;
                }
                catch(Exception e)
                {
                    result.Error = e;
                    result.LogEvent(e);
                }
            }
            else if(Environment.Is64BitProcess == true && X64 == false)
            {
                Context32 = new CONTEXT32();
                Context32.ContextFlags = CONTEXT_FLAGS.CONTEXT_ALL;
                try
                {
                    ErcCore.Wow64GetThreadContext(ThreadHandle, ref Context32);
                    if (new Win32Exception(Marshal.GetLastWin32Error()).Message != "The operation completed successfully")
                    {
                        throw new ERCException("Win32 Exception encountered when attempting to get thread context" +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }
                }
                catch (ERCException e)
                {
                    result.Error = e;
                    result.LogEvent();
                    return result;
                }
                catch (Exception e)
                {
                    result.Error = e;
                    result.LogEvent(e);
                }
            }
            else
            {
                Context32 = new CONTEXT32();
                Context32.ContextFlags = CONTEXT_FLAGS.CONTEXT_ALL;
                try
                {
                    ErcCore.GetThreadContext32(ThreadHandle, ref Context32);
                    if (new Win32Exception(Marshal.GetLastWin32Error()).Message != "The operation completed successfully")
                    {
                        throw new ERCException("Win32 Exception encountered when attempting to get thread context" +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }
                }
                catch (ERCException e)
                {
                    result.Error = e;
                    result.LogEvent();
                    return result;
                }
                catch (Exception e)
                {
                    result.Error = e;
                    result.LogEvent(e);
                }
            }
            return result;
        }
        #endregion

        #region Thread Environment Block

        #region Populate TEB
        internal ErcResult<string> PopulateTEB()
        {
            ErcResult<string> returnString = new ErcResult<string>(ThreadCore);

            var retInt = ErcCore.ZwQueryInformationThread(ThreadHandle, 0,
                ref ThreadBasicInfo, Marshal.SizeOf(typeof(ThreadBasicInformation)), IntPtr.Zero);

            if (retInt != 0)
            {
                Console.WriteLine("NTSTATUS Error was thrown: " + retInt);
                returnString.Error = new ERCException("NTSTATUS Error was thrown: " + retInt);
                return returnString;
            }

            byte[] tebBytes;
            int ret = 0;
            if(X64 == true)
            {
                tebBytes = new byte[0x16A0];
                ErcCore.ReadProcessMemory(ThreadProcess.ProcessHandle, ThreadBasicInfo.TebBaseAdress, tebBytes, 0x16A0, out ret);
            }
            else
            {
                tebBytes = new byte[3888];
                ErcCore.ReadProcessMemory(ThreadProcess.ProcessHandle, ThreadBasicInfo.TebBaseAdress, tebBytes, 3888, out ret);
            }
            

            if (ret == 0)
            {
                ERCException e = new ERCException("System error: An error occured when executing ReadProcessMemory\n Process Handle = 0x" 
                    + ThreadProcess.ProcessHandle.ToString("X") + " TEB Base Address = 0x" + ThreadBasicInfo.TebBaseAdress.ToString("X") + 
                    " Return value = " + ret);
                returnString.Error = e;
                return returnString;
            }

            if (X64 == true)
            {
                PopulateTEBStruct64(tebBytes);
            }
            else
            {
                PopulateTEBStruct32(tebBytes);
            }

            var bSehChain = BuildSehChain();
            if(bSehChain.Error != null)
            {
                returnString.Error = bSehChain.Error;
                return returnString;
            }

            return returnString;
        }
        #endregion

        #region PopulateTebStruct
        private void PopulateTEBStruct32(byte[] tebBytes)
        {
            Teb = new TEB();
            Teb.CurrentSehFrame = (IntPtr)BitConverter.ToInt64(tebBytes, 0x0);
            Teb.TopOfStack = (IntPtr)BitConverter.ToInt64(tebBytes, 0x4);
            Teb.BottomOfStack = (IntPtr)BitConverter.ToInt64(tebBytes, 0x8);
            Teb.SubSystemTeb = (IntPtr)BitConverter.ToInt64(tebBytes, 0xC);
            Teb.FiberData = (IntPtr)BitConverter.ToInt64(tebBytes, 0x10);
            Teb.ArbitraryDataSlot = (IntPtr)BitConverter.ToInt64(tebBytes, 0x14);
            Teb.Teb = (IntPtr)BitConverter.ToInt64(tebBytes, 0x18);
            Teb.EnvironmentPointer = (IntPtr)BitConverter.ToInt64(tebBytes, 0x1C);
            Teb.Identifiers.ProcessId = (IntPtr)BitConverter.ToInt64(tebBytes, 0x20);
            Teb.Identifiers.ThreadId = (IntPtr)BitConverter.ToInt64(tebBytes, 0x24);
            Teb.RpcHandle = (IntPtr)BitConverter.ToInt64(tebBytes, 0x28);
            Teb.Tls = (IntPtr)BitConverter.ToInt64(tebBytes, 0x2C);
            Teb.Peb = (IntPtr)BitConverter.ToInt64(tebBytes, 0x30);
            Teb.LastErrorNumber = BitConverter.ToInt32(tebBytes, 0x34);
            Teb.CriticalSectionsCount = BitConverter.ToInt32(tebBytes, 0x38);
            Teb.CsrClientThread = (IntPtr)BitConverter.ToInt64(tebBytes, 0x3C);
            Teb.Win32ThreadInfo = (IntPtr)BitConverter.ToInt64(tebBytes, 0x40);
            Teb.Win32ClientInfo = new byte[4];
            Array.Copy(tebBytes, 0x44, Teb.Win32ClientInfo, 0, 4);
            Teb.WoW64Reserved = (IntPtr)BitConverter.ToInt64(tebBytes, 0xC0);
            Teb.CurrentLocale = (IntPtr)BitConverter.ToInt64(tebBytes, 0xC4);
            Teb.FpSoftwareStatusRegister = (IntPtr)BitConverter.ToInt64(tebBytes, 0xC8);
            Teb.SystemReserved1 = new byte[216];
            Array.Copy(tebBytes, 0xCC, Teb.SystemReserved1, 0, 216);
            Teb.ExceptionCode = (IntPtr)BitConverter.ToInt64(tebBytes, 0x1A4);
            Teb.ActivationContextStack = new byte[4];
            Array.Copy(tebBytes, 0x1A8, Teb.ActivationContextStack, 0, 4);
            Teb.SpareBytes = new byte[24];
            Array.Copy(tebBytes, 0x1BC, Teb.SpareBytes, 0, 24);
            Teb.SystemReserved2 = new byte[40];
            Array.Copy(tebBytes, 0x1D4, Teb.SystemReserved2, 0, 40);
            Teb.GdiTebBatch = new byte[1248];
            Array.Copy(tebBytes, 0x1FC, Teb.GdiTebBatch, 0, 1248);
            Teb.GdiRegion = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6DC);
            Teb.GdiPen = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6E0);
            Teb.GdiBrush = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6E4);
            Teb.RealProcessId = BitConverter.ToInt32(tebBytes, 0x6E8);
            Teb.RealThreadId = BitConverter.ToInt32(tebBytes, 0x6EC);
            Teb.GdiCachedProcessHandle = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6F0);
            Teb.GdiClientProcessId = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6F4);
            Teb.GdiClientThreadId = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6F8);
            Teb.GdiThreadLocalInfo = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6FC);
            Teb.UserReserved1 = new byte[20];
            Array.Copy(tebBytes, 0x700, Teb.UserReserved1, 0, 20);
            Teb.GlReserved1 = new byte[1248];
            Array.Copy(tebBytes, 0x714, Teb.GlReserved1, 0, 1248);
            Teb.LastStatusValue = BitConverter.ToInt32(tebBytes, 0xBF4);
            Teb.StaticUnicodeString = new byte[214];
            Array.Copy(tebBytes, 0xBF8, Teb.StaticUnicodeString, 0, 214);
            Teb.DeallocationStack = (IntPtr)BitConverter.ToInt64(tebBytes, 0xE0C);
            Teb.TlsSlots = new byte[100];
            Array.Copy(tebBytes, 0xE10, Teb.TlsSlots, 0, 100);
            Teb.TlsLinks = BitConverter.ToInt64(tebBytes, 0xF10);
            Teb.Vdm = (IntPtr)BitConverter.ToInt64(tebBytes, 0xF18);
            Teb.RpcReserved = (IntPtr)BitConverter.ToInt64(tebBytes, 0xF1C);
            Teb.ThreadErrorMode = (IntPtr)BitConverter.ToInt64(tebBytes, 0xF28);
        }

        private void PopulateTEBStruct64(byte[] tebBytes)
        {
            Teb = new TEB();
            Teb.CurrentSehFrame = (IntPtr)BitConverter.ToInt64(tebBytes, 0x0);
            Teb.TopOfStack = (IntPtr)BitConverter.ToInt64(tebBytes, 0x8);
            Teb.BottomOfStack = (IntPtr)BitConverter.ToInt64(tebBytes, 0x10);
            Teb.SubSystemTeb = (IntPtr)BitConverter.ToInt64(tebBytes, 0x18);
            Teb.FiberData = (IntPtr)BitConverter.ToInt64(tebBytes, 0x20);
            Teb.ArbitraryDataSlot = (IntPtr)BitConverter.ToInt64(tebBytes, 0x28);
            Teb.Teb = (IntPtr)BitConverter.ToInt64(tebBytes, 0x30);
            Teb.EnvironmentPointer = (IntPtr)BitConverter.ToInt64(tebBytes, 0x38);
            Teb.Identifiers.ProcessId = (IntPtr)BitConverter.ToInt64(tebBytes, 0x40);
            Teb.Identifiers.ThreadId = (IntPtr)BitConverter.ToInt64(tebBytes, 0x48);
            Teb.RpcHandle = (IntPtr)BitConverter.ToInt64(tebBytes, 0x50);
            Teb.Tls = (IntPtr)BitConverter.ToInt64(tebBytes, 0x58);
            Teb.Peb = (IntPtr)BitConverter.ToInt64(tebBytes, 0x60);
            Teb.LastErrorNumber = BitConverter.ToInt32(tebBytes, 0x68);
            Teb.CriticalSectionsCount = BitConverter.ToInt32(tebBytes, 0x6C);
            Teb.CsrClientThread = (IntPtr)BitConverter.ToInt64(tebBytes, 0x70);
            Teb.Win32ThreadInfo = (IntPtr)BitConverter.ToInt64(tebBytes, 0x78);
            Teb.Win32ClientInfo = new byte[4];
            Array.Copy(tebBytes, 0x80, Teb.Win32ClientInfo, 0, 4);
            Teb.CurrentLocale = (IntPtr)BitConverter.ToInt64(tebBytes, 0x84);
            Teb.FpSoftwareStatusRegister = (IntPtr)BitConverter.ToInt64(tebBytes, 0x8C);
            Teb.SystemReserved1 = new byte[216];
            Array.Copy(tebBytes, 0x94, Teb.SystemReserved1, 0, 216);
            Teb.ExceptionCode = (IntPtr)BitConverter.ToInt64(tebBytes, 0x16C);
            Teb.ActivationContextStack = new byte[4];
            Array.Copy(tebBytes, 0x174, Teb.ActivationContextStack, 0, 4);
            Teb.SpareBytes = new byte[24];
            Array.Copy(tebBytes, 0x178, Teb.SpareBytes, 0, 24);
            Teb.SystemReserved2 = new byte[40];
            Array.Copy(tebBytes, 0x190, Teb.SystemReserved2, 0, 40);
            Teb.GdiTebBatch = new byte[1248];
            Array.Copy(tebBytes, 0x1b8, Teb.GdiTebBatch, 0, 1248);
            Teb.GdiRegion = (IntPtr)BitConverter.ToInt64(tebBytes, 0x698);
            Teb.GdiPen = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6A0);
            Teb.GdiBrush = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6A8);
            Teb.RealProcessId = BitConverter.ToInt32(tebBytes, 0x6B0);
            Teb.RealThreadId = BitConverter.ToInt32(tebBytes, 0x6B4);
            Teb.GdiCachedProcessHandle = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6B8);
            Teb.GdiClientProcessId = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6C0);
            Teb.GdiClientThreadId = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6C8);
            Teb.GdiThreadLocalInfo = (IntPtr)BitConverter.ToInt64(tebBytes, 0x6D0);
            Teb.UserReserved1 = new byte[20];
            Array.Copy(tebBytes, 0x6D8, Teb.UserReserved1, 0, 20);
            Teb.GlReserved1 = new byte[1248];
            Array.Copy(tebBytes, 0x6EC, Teb.GlReserved1, 0, 1248);
            Teb.LastStatusValue = BitConverter.ToInt32(tebBytes, 0x1250);
            Teb.StaticUnicodeString = new byte[214];
            Array.Copy(tebBytes, 0x1258, Teb.StaticUnicodeString, 0, 214);
            Teb.DeallocationStack = (IntPtr)BitConverter.ToInt64(tebBytes, 0x1478);
            Teb.TlsSlots = new byte[520];
            Array.Copy(tebBytes, 0x1480, Teb.TlsSlots, 0, 520);
            Teb.TlsLinks = BitConverter.ToInt64(tebBytes, 0x1680);
            Teb.Vdm = (IntPtr)BitConverter.ToInt64(tebBytes, 0x1688);
            Teb.RpcReserved = (IntPtr)BitConverter.ToInt64(tebBytes, 0x1690);
            Teb.ThreadErrorMode = (IntPtr)BitConverter.ToInt64(tebBytes, 0x1698);
        }
        #endregion

        #region BuildSehChain
        internal ErcResult<List<byte[]>> BuildSehChain()
        {
            ErcResult<List<byte[]>> sehList = new ErcResult<List<byte[]>>(ThreadCore);
            sehList.ReturnValue = new List<byte[]>();

            if(Teb.Equals(default(TEB)))
            {
                sehList.Error = new Exception("Error: TEB structure for this thread has not yet been populated. Call PopulateTEB first");
                return sehList;
            }

            if(Teb.CurrentSehFrame == IntPtr.Zero)
            {
                sehList.Error = new Exception("Error: No SEH chain has been generated yet. An SEH chain will not be generated until a crash occurs.");
                return sehList;
            }

            byte[] sehEntry;
            byte[] sehFinal;

            int arraySize = 0;
            if(X64 == true)
            {
                arraySize = 8;
                sehEntry = new byte[arraySize];
                sehFinal = new byte[arraySize];
                sehEntry = BitConverter.GetBytes((long)Teb.CurrentSehFrame);
            }
            else
            {
                arraySize = 4;
                sehEntry = new byte[arraySize];
                sehFinal = new byte[arraySize];
                sehEntry = BitConverter.GetBytes((int)Teb.CurrentSehFrame);
            }
            
            for (int i = 0; i < sehFinal.Length; i++)
            {
                sehFinal[i] = 0xFF;
            }

            while (!sehEntry.SequenceEqual(sehFinal))
            {
                byte[] reversedSehEntry = new byte[arraySize];
                int ret = 0;

                if(X64 == true)
                {
                    ret = ErcCore.ReadProcessMemory(ThreadProcess.ProcessHandle, (IntPtr)BitConverter.ToInt64(sehEntry, 0), sehEntry, arraySize, out int retInt);
                }
                else
                {
                    ret = ErcCore.ReadProcessMemory(ThreadProcess.ProcessHandle, (IntPtr)BitConverter.ToInt32(sehEntry, 0), sehEntry, arraySize, out int retInt);
                }


                if(ret != 0 && ret != 1)
                {
                    ERCException e = new ERCException("System error: An error occured when executing ReadProcessMemory\n Process Handle = 0x"
                    + ThreadProcess.ProcessHandle.ToString("X") + " TEB Current Seh = 0x" + Teb.CurrentSehFrame.ToString("X") +
                    " Return value = " + ret + Environment.NewLine + "Win32Exception: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    sehList.Error = e;
                    sehList.LogEvent();
                    return sehList;
                }

                for(int i = 0; i < sehEntry.Length; i++)
                {
                    reversedSehEntry[i] = sehEntry[i];
                }
                Array.Reverse(reversedSehEntry, 0, reversedSehEntry.Length);

                if (!sehEntry.SequenceEqual(sehFinal))
                {
                    sehList.ReturnValue.Add(reversedSehEntry);
                }
            }
            SehChain = new List<byte[]>(sehList.ReturnValue);

            return sehList;
        }
        #endregion

        #endregion

        #region Public Methods
        /// <summary>
        /// Gets the current SEH chain for the process.
        /// </summary>
        /// <returns>Returns a list of IntPtr containing the SEH chain</returns>
        public List<IntPtr> GetSehChain()
        {
            List<IntPtr> SehPtrs = new List<IntPtr>();
            var pteb = PopulateTEB();
            if (pteb.Error != null)
            {
                throw pteb.Error;
            }

            if(SehChain == null)
            {
                throw new Exception("Error: No SEH chain has been generated yet. An SEH chain will not be generated until a crash occurs.");
            }

            if(SehChain.Count == 0)
            {
                throw new Exception("Error: No SEH chain has been generated yet. An SEH chain will not be generated until a crash occurs.");
            }

            if(X64 == true)
            {
                for (int i = 0; i < SehChain.Count; i++)
                {
                    SehPtrs.Add((IntPtr)BitConverter.ToInt64(SehChain[i], 0));
                }
            }
            else
            {
                for (int i = 0; i < SehChain.Count; i++)
                {
                    SehPtrs.Add((IntPtr)BitConverter.ToInt32(SehChain[i], 0));
                }
            }
            return SehPtrs;
        }

        /// <summary>
        /// Gets the Thread environment block of the current thread.
        /// </summary>
        /// <returns>Returns a TEB struct</returns>
        public TEB GetTeb()
        {
            if (Teb.Equals(default(TEB)))
            {
                throw new Exception("Error: TEB structure for this thread has not yet been populated. Call PopulateTEB first");
            }
            return Teb;
        }
        #endregion
    }
}
