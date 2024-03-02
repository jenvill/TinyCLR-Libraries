using System;
using System.Text;
using System.Threading;

namespace GHIElectronics.TinyCLR.Data.Json
{
	public abstract class JToken
	{
		private bool _fOwnsContext;

		protected void EnterSerialization(JsonSerializationOptions options = null)
		{
            if (options == null)
            {
                options = new JsonSerializationOptions();
            }

			lock (JsonConverter.SyncObj)
			{
				if (JsonConverter.SerializationContext == null)
				{
					JsonConverter.SerializationContext = new JsonConverter.SerializationCtx(options);
					JsonConverter.SerializationContext.IndentLevel = 0;
					Monitor.Enter(JsonConverter.SerializationContext);
					_fOwnsContext = true;
				}
			}
		}

		protected void ExitSerialization()
		{
			lock (JsonConverter.SyncObj)
			{
				if (_fOwnsContext)
				{
					var monitorObj = JsonConverter.SerializationContext;
					JsonConverter.SerializationContext = null;
					_fOwnsContext = false;
					Monitor.Exit(monitorObj);
				}
			}
		}

		protected string Indent(bool incrementAfter = false)
		{
            if (!JsonConverter.SerializationContext.options.Indented)
            {
                return string.Empty;
            }

			FixedStringBuilder sb = new FixedStringBuilder();
			string indent = "  ";
			if (JsonConverter.SerializationContext != null)
			{
				for (int i = 0; i < JsonConverter.SerializationContext.IndentLevel; ++i)
					sb.Append(indent);
				if (incrementAfter)
					++JsonConverter.SerializationContext.IndentLevel;
			}
			return sb.ToString();
		}

		protected void Outdent()
		{
			--JsonConverter.SerializationContext.IndentLevel;
		}

		public byte[] ToBson()
		{
			var size = this.GetBsonSize("") + 5;
			var buffer = new byte[size];
			int offset = 4;
            this.ToBson("", buffer, ref offset);

            // Write the trailing nul
            buffer[offset++] = (byte)0;

            // Write the completed size
            int zero = 0;
            SerializationUtilities.Marshall(buffer, ref zero, offset);
            return buffer;
		}

        public abstract BsonTypes GetBsonType();

		public abstract int GetBsonSize();

		public abstract int GetBsonSize(string ename);

		public abstract void ToBson(byte[] buffer, ref int offset);

        public void ToBson(string ename, byte[] buffer, ref int offset)
        {
#if DEBUG
            int startingOffset = offset;
#endif

            if (buffer!=null)
                buffer[offset] = (byte)this.GetBsonType();
            ++offset;

            MarshallEName(ename, buffer, ref offset);
            ToBson(buffer, ref offset);

#if DEBUG
            if (this.GetBsonSize(ename) != (offset - startingOffset))
                throw new Exception("marshalling error");
#endif
        }

        protected void MarshallEName(string ename, byte[] buffer, ref int offset)
        {
            var name = Encoding.UTF8.GetBytes(ename);
            if (buffer != null && ename.Length > 0)
                Array.Copy(name, 0, buffer, offset, name.Length);
            offset += name.Length;
            if (buffer != null)
                buffer[offset] = 0;
            ++offset;
        }

        internal static String ConvertToString(Byte[] byteArray, int start, int count)
        {
            var _chars = new char[byteArray.Length];
            bool _completed;
            int _bytesUsed, _charsUsed;
            Encoding.UTF8.GetDecoder().Convert(byteArray, start, count, _chars, 0, byteArray.Length, false, out _bytesUsed, out _charsUsed, out _completed);
            return new string(_chars, 0, _charsUsed);
        }

        internal static int FindNul(byte[] buffer, int start)
        {
            int current = start;
            while (current < buffer.Length)
            {
                if (buffer[current++] == 0)
                    return current - 1;
            }
            return -1;
        }

        public abstract string ToString(JsonSerializationOptions options);
    }
}
