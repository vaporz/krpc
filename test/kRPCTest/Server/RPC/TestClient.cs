using System;
using KRPC.Server;

namespace KRPCTest.Server.RPC
{
    // TODO: This is only required due to mocking not preforming equality testing. Is there a better way to do this?
    class TestClient : IClient<byte,byte>
    {
        Guid guid;

        public TestClient (TestStream stream)
        {
            guid = Guid.NewGuid();
            Stream = stream;
        }

        public string Name {
            get { throw new NotImplementedException (); }
        }

        public Guid Guid {
            get { return guid; }
        }

        public string Address {
            get { throw new NotImplementedException (); }
        }

        public IStream<byte,byte> Stream { get; private set; }

        public bool Connected {
            get { throw new NotImplementedException (); }
        }

        public void Close ()
        {
            throw new NotImplementedException ();
        }

        public override bool Equals (Object obj)
        {
            return obj != null && Equals (obj as TestClient);
        }

        public bool Equals (IClient<byte,byte> other)
        {
            if ((object)other == null)
                return false;
            var otherClient = other as TestClient;
            if ((object)otherClient == null)
                return false;
            return guid == otherClient.guid;
        }

        public override int GetHashCode ()
        {
            return guid.GetHashCode ();
        }

        public static bool operator == (TestClient lhs, TestClient rhs)
        {
            if (Object.ReferenceEquals (lhs, rhs))
                return true;
            if ((object)lhs == null || (object)rhs == null)
                return false;
            return lhs.Equals (rhs);
        }

        public static bool operator != (TestClient lhs, TestClient rhs)
        {
            return !(lhs == rhs);
        }
    }
}
