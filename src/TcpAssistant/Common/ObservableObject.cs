using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace TcpAssistant.Common
{
    /// <summary>
    /// INotifyPropertyChanged 的最小实现，给模型和 ViewModel 复用。
    /// 标 [DataContract] 是为了让派生模型（ConnectionProfile / CommandItem）
    /// 能被 DataContractJsonSerializer 正常序列化——它要求基类也带契约标记。
    /// </summary>
    [DataContract]
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }
}
