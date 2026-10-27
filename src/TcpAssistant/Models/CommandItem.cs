using System.Runtime.Serialization;
using TcpAssistant.Common;

namespace TcpAssistant.Models
{
    /// <summary>
    /// 一条快捷指令：既是“点一下就发”的按钮，也是协议的书面说明。
    /// </summary>
    [DataContract]
    public sealed class CommandItem : ObservableObject
    {
        private string _name = "新指令";
        private string _text = string.Empty;
        private bool _isHex;
        private bool _appendCrLf;
        private bool _expectReply = true;
        private string _note = string.Empty;

        [DataMember(Order = 1)]
        public string Name
        {
            get { return _name; }
            set { Set(ref _name, value); Raise(nameof(Caption)); }
        }

        /// <summary>指令正文；文本模式下支持 \r\n \x41 这类转义。</summary>
        [DataMember(Order = 2)]
        public string Text
        {
            get { return _text; }
            set { Set(ref _text, value); Raise(nameof(Caption)); }
        }

        /// <summary>true 表示 Text 里写的是 HEX。</summary>
        [DataMember(Order = 3)]
        public bool IsHex
        {
            get { return _isHex; }
            set { Set(ref _isHex, value); }
        }

        [DataMember(Order = 4)]
        public bool AppendCrLf
        {
            get { return _appendCrLf; }
            set { Set(ref _appendCrLf, value); }
        }

        /// <summary>发出后是否等待回包（等待超时会在日志里标红）。</summary>
        [DataMember(Order = 5)]
        public bool ExpectReply
        {
            get { return _expectReply; }
            set { Set(ref _expectReply, value); }
        }

        [DataMember(Order = 6)]
        public string Note
        {
            get { return _note; }
            set { Set(ref _note, value); }
        }

        /// <summary>列表里显示的一行：指令名 + 正文摘要。</summary>
        public string Caption
        {
            get
            {
                var body = (_text ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
                if (body.Length > 28) body = body.Substring(0, 28) + "…";
                return string.IsNullOrEmpty(body) ? _name : _name + "   " + body;
            }
        }

        public CommandItem Clone()
        {
            return new CommandItem
            {
                Name = Name,
                Text = Text,
                IsHex = IsHex,
                AppendCrLf = AppendCrLf,
                ExpectReply = ExpectReply,
                Note = Note
            };
        }
    }
}
