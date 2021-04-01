// Class Files

using myseq;
using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Structures

{
    [Flags]
    public enum RequestType
    {
        None = 0,
        //Bit Flags determining what data to send to the client
        ZONE = 0x00000001,
        PLAYER = 0x00000002,
        TARGET = 0x00000004,
        MOBS = 0x00000008,
        GROUND_ITEMS = 0x00000010,
        GET_PROCESSINFO = 0x00000020,
        SET_PROCESSINFO = 0x00000040,
        WORLD = 0x00000080
    }

    public class EQCommunications

    {
        private const string ServConErr = "Server Connection Error";

        // Variables to store any incomplete packets till the next chunk arrives.

        private int incompleteCount = 0;

        private bool RequestPending;

        private CSocketClient pSocketClient;

        private bool update_hidden;

        private bool mbGetProcessInfo;
        private bool send_process;

        private int numPackets; // Total Packets expected

        private int numProcessed; // No. of Packets already processed        

        private readonly byte[] incompletebuffer = new byte[2048];

        private readonly EQData eq;

        private readonly FrmMain f1; // TODO: get rid of this

        public int NewProcessID { get; set; }

        public void UpdateHidden()
        {
            update_hidden = true;
        }

        public EQCommunications(EQData eq, FrmMain f1)

        {
            this.eq = eq;

            this.f1 = f1;
        }

        public void StopListening()

        {
            try

            {
                RequestPending = false;

                numPackets = numProcessed = 0;

                pSocketClient?.Disconnect();

                pSocketClient = null;
            }
            catch (Exception pException) { LogLib.WriteLine($"Error: StopListening: {pException.Message}"); }
        }

        public bool ConnectToServer(string ServerAddress, int ServerPort, bool errMsg = true)

        {
            try
            {
                pSocketClient?.Disconnect();

                // Instantiate a CSocketClient object

                pSocketClient = new CSocketClient(100000,
                    new CSocketClient.MESSAGE_HANDLER(MessageHandlerClient),
                    new CSocketClient.CLOSE_HANDLER(CloseHandler)
                    );

                // Establish a connection to the server
                mbGetProcessInfo = true;
                pSocketClient.Connect(ServerAddress, ServerPort);

                return true;
            }
            catch (Exception pException)
            {
                var msg = $"{ServConErr} {pException.Message}";
                LogLib.WriteLine(msg);
                if (errMsg)
                {
                    MessageBox.Show(
                        msg
                        + "\r\nTry selecting a different server!",
                        caption: ServConErr,
                        buttons: MessageBoxButtons.OK,
                        icon: MessageBoxIcon.Error);
                }
                return false;
            }
        }

        //********************************************************************

        /// <summary> Called when a message is extracted from the socket </summary>
        /// <param name="pSocket"> The SocketClient object the message came from </param>
        /// <param name="iNumberOfBytes"> The number of bytes in the RawBuffer inside the SocketClient </param>
        private void MessageHandlerClient(CSocketClient pSocket, int iNumberOfBytes)
        {
            // Process the packet
            ProcessPacket(pSocket.GetRawBuffer, iNumberOfBytes);
        }

        //********************************************************************

        /// <summary> Called when a socket connection is closed </summary>
        /// <param name="pSocket"> The SocketClient object the message came from </param>
        private void CloseHandler(CSocketClient pSocket)
        {
                if (f1 == null)
                    StopListening();
                else
                    f1.StopListening();
        }

        //********************************************************************

        private void SendData(byte[] data)
        {
            pSocketClient.Send(data);
        }

        public void Tick()
        {
            int Request;

            try
            {
                if (!RequestPending)
                {
                    if (NewProcessID > 0 && !mbGetProcessInfo)
                    {
                        if (!send_process)
                        {
                            // We have a request to change the process

                            Request = (int)RequestType.SET_PROCESSINFO;
                            SendData(BitConverter.GetBytes(Request));
                            send_process = true;
                        }
                        else
                        {
                            SendData(BitConverter.GetBytes(NewProcessID));
                            send_process = false;
                            NewProcessID = 0;
                            mbGetProcessInfo = true;
                        }
                    }
                    else
                    {
                        RequestPending = true;
                        Request = (int)(RequestType.ZONE
                                        | RequestType.PLAYER
                                        | RequestType.TARGET
                                        | RequestType.MOBS
                                        | RequestType.GROUND_ITEMS
                                        | RequestType.WORLD);

                        if (mbGetProcessInfo && NewProcessID == 0)
                        {
                            mbGetProcessInfo = false;
                            Request |= (int)RequestType.GET_PROCESSINFO;
                        }

                        SendData(BitConverter.GetBytes(Request));
                    }
                }
            }
            catch (Exception ex) { LogLib.WriteLine("Error: timPackets_Tick: ", ex); }
        }

        public void CharRefresh()
        {
            if (pSocketClient != null)
                mbGetProcessInfo = true;
        }

        public void SwitchCharacter(ProcessInfo PI)
        {
            if (PI?.ProcessID > 0)
                NewProcessID = PI.ProcessID;
        }

        public bool CanSwitchChars() => NewProcessID == 0 && !mbGetProcessInfo;

        private void ProcessPacket(byte[] packet, int bytes)
        {
            var offset = 0;

            const int SIZE_OF_PACKET = 100; //104 on new server

            try
            {
                if (bytes > 0)

                {
                    // we have received some bytes, check if this is the beginning of a new packet or a chunk of an existing one.

                    offset = CheckStart(packet);

                    eq.BeginProcessPacket(); //clears spawn&ground arrays

                    for (; offset + SIZE_OF_PACKET <= bytes; offset += SIZE_OF_PACKET)

                    {
                        SPAWNINFO si = new SPAWNINFO();

                        if (offset < 0)
                        {
                            // copy the missing chunk of the incomplete packet to the incomplete packet buffer
                            try
                            {
                                PacketCopy(packet, SIZE_OF_PACKET);
                            }
                            catch (Exception ex) { LogLib.WriteLine("Error: ProcessPacket: Copy Incomplete packet buffer: ", ex); }
                            incompleteCount = 0;
                            if (incompletebuffer.Length == 0)
                            {
                                numPackets = 0;
                                break;
                            }
                            si.Frombytes(incompletebuffer, 0);
                        }
                        else
                        {
                            si.Frombytes(packet, offset);
                        }

                        numProcessed++;
                        f1.ProcessPacket(si, update_hidden);
                    }

                    eq.ProcessSpawnList(f1.SpawnList);
                    eq.ProcessGroundItemList(f1.GroundItemList);
                }
            }
            catch (Exception ex) { LogLib.WriteLine("Error: ProcessPacket: ", ex); }

            ProcessedPackets(packet, bytes, offset);
        }

        private int CheckStart(byte[] packet)
        {
            int offset;
            if (numPackets == 0)
            {
                // The first word in the data stream is the number of packets

                numPackets = BitConverter.ToInt32(packet, 0);
                offset = 4;
                f1.StartNewPackets();
            }
            else
            {
                // We havent finished processing packets, so check if we have any extra bytes stored in our incomplete buffer.

                offset = -incompleteCount;
            }

            return offset;
        }

        private void ProcessedPackets(byte[] packet, int bytes, int offset)
        {
            if (numProcessed < numPackets)
            {
                if (offset < bytes)
                {
                    // Copy unprocessed bytes into the incomplete buffer
                    IncompleteCopy(packet, bytes, offset);
                }
            }
            else
            {
                // Finished proceessing the request
                FinalizeProcess();

                f1.CheckMobs();
                f1.mapCon.Invalidate();
            }
        }

        private void FinalizeProcess()
        {
            RequestPending = false;
            if (update_hidden)
                update_hidden = false;
            numPackets = numProcessed = 0;

            incompleteCount = 0;
            // Make sure that the incomplete buffer is actually empty
            if (incompletebuffer.Length > 0)
            {
                for (var pp = 0; pp < incompletebuffer.Length; pp++)
                {
                    incompletebuffer[pp] = 0;
                }
            }
        }

        private void IncompleteCopy(byte[] packet, int bytes, int offset)
        {
            incompleteCount = bytes - offset;

            try
            {
                Array.Copy(packet, offset, incompletebuffer, 0, incompleteCount);
            }
            catch (Exception ex)
            {
                LogLib.WriteLine("Error: ProcessPacket(): Copy to Incomplete Buffer: ", ex);
                LogLib.WriteLine($"Packet Size: {packet.Length} Offset: {offset}");
                LogLib.WriteLine($"Buffer Size: {incompletebuffer.Length} Incomplete Size: {incompleteCount}");
            }
        }

        private void PacketCopy(byte[] packet, int SIZE_OF_PACKET)
        {
            if (incompleteCount > 0 && packet.Length > 0)
                Array.Copy(packet, 0, incompletebuffer, incompleteCount, SIZE_OF_PACKET - incompleteCount);
        }
    }
}