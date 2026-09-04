using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HDRCalibrate.Dwm
{
    public sealed class D3DLibraryInfo
    {
        public string Creator { get; internal set; }
        public uint Flags { get; internal set; }
        public IReadOnlyList<D3DFunctionInfo> Functions { get; internal set; }
    }

    public sealed class D3DFunctionInfo
    {
        public uint Version { get; internal set; }
        public uint Flags { get; internal set; }
        public uint MinFeatureLevel { get; internal set; }
        public ulong RequiredFeatureFlags { get; internal set; }
        public string Name { get; internal set; }
        public bool HasReturn { get; internal set; }
        public bool Has10Level9VertexShader { get; internal set; }
        public bool Has10Level9PixelShader { get; internal set; }
        public IReadOnlyList<D3DParameterInfo> Parameters { get; internal set; }
        public D3DParameterInfo ReturnParameter { get; internal set; }
    }

    public sealed class D3DParameterInfo
    {
        public string Name { get; internal set; }
        public string SemanticName { get; internal set; }
        public int Type { get; internal set; }
        public int Class { get; internal set; }
        public uint Rows { get; internal set; }
        public uint Columns { get; internal set; }
        public int InterpolationMode { get; internal set; }
        public int Flags { get; internal set; }
        public uint FirstInRegister { get; internal set; }
        public uint FirstInComponent { get; internal set; }
        public uint FirstOutRegister { get; internal set; }
        public uint FirstOutComponent { get; internal set; }
    }

    public static class D3DLibraryReflection
    {
        private static readonly Guid LibraryReflectionIid =
            new Guid("54384F1B-5B3E-4BB7-AE01-60BA3097CBB6");

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeLibraryDesc
        {
            public IntPtr Creator;
            public uint Flags;
            public uint FunctionCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFunctionDesc
        {
            public uint Version;
            public IntPtr Creator;
            public uint Flags;
            public uint ConstantBuffers;
            public uint BoundResources;
            public uint InstructionCount;
            public uint TempRegisterCount;
            public uint TempArrayCount;
            public uint DefCount;
            public uint DclCount;
            public uint TextureNormalInstructions;
            public uint TextureLoadInstructions;
            public uint TextureCompInstructions;
            public uint TextureBiasInstructions;
            public uint TextureGradientInstructions;
            public uint FloatInstructionCount;
            public uint IntInstructionCount;
            public uint UintInstructionCount;
            public uint StaticFlowControlCount;
            public uint DynamicFlowControlCount;
            public uint MacroInstructionCount;
            public uint ArrayInstructionCount;
            public uint MovInstructionCount;
            public uint MovcInstructionCount;
            public uint ConversionInstructionCount;
            public uint BitwiseInstructionCount;
            public uint MinFeatureLevel;
            public ulong RequiredFeatureFlags;
            public IntPtr Name;
            public int FunctionParameterCount;
            public int HasReturn;
            public int Has10Level9VertexShader;
            public int Has10Level9PixelShader;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeParameterDesc
        {
            public IntPtr Name;
            public IntPtr SemanticName;
            public int Type;
            public int Class;
            public uint Rows;
            public uint Columns;
            public int InterpolationMode;
            public int Flags;
            public uint FirstInRegister;
            public uint FirstInComponent;
            public uint FirstOutRegister;
            public uint FirstOutComponent;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LibraryGetDesc(IntPtr instance, out NativeLibraryDesc description);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr LibraryGetFunctionByIndex(IntPtr instance, int index);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FunctionGetDesc(IntPtr instance, out NativeFunctionDesc description);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr FunctionGetParameter(IntPtr instance, int index);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ParameterGetDesc(IntPtr instance, out NativeParameterDesc description);

        [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DReflectLibrary(
            IntPtr sourceData,
            UIntPtr sourceDataSize,
            [In] ref Guid interfaceId,
            out IntPtr reflector);

        public static D3DLibraryInfo Reflect(byte[] libraryBytes)
        {
            if (libraryBytes == null || libraryBytes.Length < 32)
                throw new ArgumentException("D3D-Bibliotheksdaten fehlen.", nameof(libraryBytes));

            var pin = GCHandle.Alloc(libraryBytes, GCHandleType.Pinned);
            IntPtr library = IntPtr.Zero;
            try
            {
                var iid = LibraryReflectionIid;
                var hr = D3DReflectLibrary(
                    pin.AddrOfPinnedObject(),
                    new UIntPtr(checked((uint)libraryBytes.Length)),
                    ref iid,
                    out library);
                if (hr < 0 || library == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "D3DReflectLibrary hat die Shaderbibliothek abgelehnt (HRESULT 0x" + hr.ToString("X8") + ").");

                var getLibraryDesc = GetVtableDelegate<LibraryGetDesc>(library, 3);
                if (getLibraryDesc(library, out var nativeLibrary) < 0)
                    throw new InvalidOperationException("D3DReflectLibrary konnte die Bibliotheksbeschreibung nicht lesen.");

                var functions = new List<D3DFunctionInfo>(checked((int)nativeLibrary.FunctionCount));
                var getFunction = GetVtableDelegate<LibraryGetFunctionByIndex>(library, 4);
                for (var index = 0; index < nativeLibrary.FunctionCount; index++)
                {
                    var function = getFunction(library, checked((int)index));
                    if (function == IntPtr.Zero)
                        throw new InvalidOperationException("D3DReflectLibrary lieferte keine Funktion für Index " + index + ".");
                    functions.Add(ReadFunction(function));
                }

                return new D3DLibraryInfo
                {
                    Creator = PtrToString(nativeLibrary.Creator),
                    Flags = nativeLibrary.Flags,
                    Functions = functions
                };
            }
            finally
            {
                if (library != IntPtr.Zero) Marshal.Release(library);
                pin.Free();
            }
        }

        private static D3DFunctionInfo ReadFunction(IntPtr function)
        {
            var getDescription = GetVtableDelegate<FunctionGetDesc>(function, 0);
            if (getDescription(function, out var native) < 0)
                throw new InvalidOperationException("Eine D3D-Bibliotheksfunktion konnte nicht reflektiert werden.");
            if (native.FunctionParameterCount < 0 || native.FunctionParameterCount > 1024)
                throw new InvalidOperationException("D3DReflectLibrary meldet eine ungültige Parameteranzahl.");

            var getParameter = GetVtableDelegate<FunctionGetParameter>(function, 6);
            var parameters = new List<D3DParameterInfo>(native.FunctionParameterCount);
            for (var index = 0; index < native.FunctionParameterCount; index++)
                parameters.Add(ReadParameter(getParameter(function, index), index));

            return new D3DFunctionInfo
            {
                Version = native.Version,
                Flags = native.Flags,
                MinFeatureLevel = native.MinFeatureLevel,
                RequiredFeatureFlags = native.RequiredFeatureFlags,
                Name = PtrToString(native.Name),
                HasReturn = native.HasReturn != 0,
                Has10Level9VertexShader = native.Has10Level9VertexShader != 0,
                Has10Level9PixelShader = native.Has10Level9PixelShader != 0,
                Parameters = parameters,
                ReturnParameter = native.HasReturn != 0 ? ReadParameter(getParameter(function, -1), -1) : null
            };
        }

        private static D3DParameterInfo ReadParameter(IntPtr parameter, int index)
        {
            if (parameter == IntPtr.Zero)
                throw new InvalidOperationException("D3DReflectLibrary lieferte keinen Parameter für Index " + index + ".");
            var getDescription = GetVtableDelegate<ParameterGetDesc>(parameter, 0);
            if (getDescription(parameter, out var native) < 0)
                throw new InvalidOperationException("Ein D3D-Funktionsparameter konnte nicht reflektiert werden.");
            return new D3DParameterInfo
            {
                Name = PtrToString(native.Name),
                SemanticName = PtrToString(native.SemanticName),
                Type = native.Type,
                Class = native.Class,
                Rows = native.Rows,
                Columns = native.Columns,
                InterpolationMode = native.InterpolationMode,
                Flags = native.Flags,
                FirstInRegister = native.FirstInRegister,
                FirstInComponent = native.FirstInComponent,
                FirstOutRegister = native.FirstOutRegister,
                FirstOutComponent = native.FirstOutComponent
            };
        }

        private static T GetVtableDelegate<T>(IntPtr instance, int slot) where T : class
        {
            var vtable = Marshal.ReadIntPtr(instance);
            var method = Marshal.ReadIntPtr(vtable, checked(slot * IntPtr.Size));
            return (T)(object)Marshal.GetDelegateForFunctionPointer(method, typeof(T));
        }

        private static string PtrToString(IntPtr value)
        {
            return value == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(value);
        }
    }
}
