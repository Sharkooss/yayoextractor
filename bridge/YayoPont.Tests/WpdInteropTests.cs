using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using YayoPont.Phone;
using Xunit;

namespace YayoPont.Tests
{
    public class WpdInteropTests
    {
        /// <summary>
        /// Régression du 23/10/2026 : en interop COM, un tableau sans
        /// [MarshalAs(LPArray)] part en SAFEARRAY ; WPD y écrivait comme dans un
        /// tableau C et le pont plantait dès qu'un téléphone était branché.
        /// Aucun test sans appareil ne le voyait : on vérifie donc les signatures.
        /// </summary>
        [Fact]
        public void Tout_tableau_des_interfaces_WPD_est_un_tableau_C()
        {
            var interfaces = typeof(IPortableDeviceManager).Assembly.GetTypes()
                .Where(t => t.IsInterface && t.GetCustomAttribute<ComImportAttribute>() != null)
                .ToList();
            Assert.NotEmpty(interfaces);
            var offenders = from type in interfaces
                            from method in type.GetMethods()
                            from param in method.GetParameters()
                            let paramType = param.ParameterType.IsByRef ? param.ParameterType.GetElementType() : param.ParameterType
                            where paramType.IsArray
                            let marshal = param.GetCustomAttribute<MarshalAsAttribute>()
                            where marshal == null || marshal.Value != UnmanagedType.LPArray
                            select $"{type.Name}.{method.Name}({param.Name})";
            Assert.Empty(offenders);
        }
    }
}
