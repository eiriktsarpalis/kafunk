﻿module CodecTests

open NUnit.Framework

open System.Text

open Kafunk

let arraySegToFSharp (arr:Binary.Segment) =
  let sb = StringBuilder()
  sb.Append("[| ") |> ignore
  for byte in arr do
    sb.AppendFormat("{0}uy;", byte) |> ignore
  sb.Append(" |]") |> ignore
  sb.ToString()

[<Test>]
let ``Binary.writeInt32 should encode int32 negative``() =
  let x = -1
  let buf = Binary.zeros 4
  Binary.writeInt32 x buf |> ignore
  let x2 = Binary.readInt32 buf |> fst
  Assert.AreEqual(x, x2)

let toArraySeg (size:'a -> int) (write:'a * BinaryZipper -> unit) (a:'a) =
  let size = size a
  let buf = Binary.zeros size
  let bz = BinaryZipper (buf)
  write (a,bz)
  buf

[<Test>]
let ``Crc.crc32 message``() =
  let messageVer = 0s
  let m = Message.create (Binary.ofArray "hello world"B) (Binary.empty) None
  let bytes = toArraySeg (fun m -> Message.Size (messageVer,m)) (fun (m,buf) -> Message.Write (messageVer, m,buf)) m
  let crc32 = Crc.crc32 bytes.Array (bytes.Offset + 4) (bytes.Count - 4)
  let expected = 1940715388u
  Assert.AreEqual(expected, crc32)

[<Test>]
let ``Message.ComputeCrc``() =
  let messageVer = 0s
  let m = Message.create (Binary.ofArray "hello world"B) (Binary.empty) None
  //let bytes = toArraySeg (Message.size messageVer) (Message.write messageVer) m
  let bytes = toArraySeg (fun m -> Message.Size (messageVer,m)) (fun (m,buf) -> Message.Write (messageVer, m,buf)) m
  let m2 = Message.Read (0s, BinaryZipper(bytes))
  let crc32 = Message.ComputeCrc (messageVer, m2)
  let expected = int 1940715388u
  Assert.AreEqual(expected, m2.crc)
  Assert.AreEqual(expected, crc32)
  

[<Test>]
let ``Crc.crc32 string``() =
  let bytes = "hello world"B
  let crc32 = Crc.crc32 bytes 0 bytes.Length
  let expected = 0x0D4A1185u
  Assert.AreEqual(expected, crc32)

[<Test>]
let ``MessageSet.write should encode MessageSet``() =
  let expected =
    [
        0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 16uy; 45uy;
        70uy; 24uy; 62uy; 0uy; 0uy; 0uy; 0uy; 0uy; 1uy; 49uy; 0uy; 0uy; 0uy;
        1uy; 48uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 16uy;
        90uy; 65uy; 40uy; 168uy; 0uy; 0uy; 0uy; 0uy; 0uy; 1uy; 49uy; 0uy; 0uy;
        0uy; 1uy; 49uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy;
        16uy; 195uy; 72uy; 121uy; 18uy; 0uy; 0uy; 0uy; 0uy; 0uy; 1uy; 49uy; 0uy;
        0uy; 0uy; 1uy; 50uy
    ]
  let ms =
    [
      Message.create (Binary.ofArray "0"B) (Binary.ofArray "1"B) None
      Message.create (Binary.ofArray "1"B) (Binary.ofArray "1"B) None
      Message.create (Binary.ofArray "2"B) (Binary.ofArray "1"B) None
    ]
    |> MessageSet.ofMessages 0s
  let size = MessageSet.Size (0s,ms)
  let data = Binary.zeros size
  let bz = BinaryZipper (data)
  MessageSet.Write (0s, ms, bz)
  //let data = toArraySeg (MessageSet.size 0s) (MessageSet.write 0s) ms
  let encoded = data |> Binary.toArray |> Array.toList
  Assert.True ((expected = encoded))


let FetchResponseBinary = 
  Binary.ofArray [|
    0uy;0uy;0uy;1uy;0uy;4uy;116uy;101uy;115uy;116uy;0uy;0uy;0uy;1uy;0uy;0uy;
    0uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;8uy;0uy;0uy;0uy;37uy;0uy;0uy;
    0uy;0uy;0uy;0uy;0uy;7uy;0uy;0uy;0uy;25uy;115uy;172uy;247uy;124uy;0uy;0uy;
    255uy;255uy;255uy;255uy;0uy;0uy;0uy;11uy;104uy;101uy;108uy;108uy;111uy;
    32uy;119uy;111uy;114uy;108uy;100uy; |]

[<Test>]
let ``FetchResponse.read should decode FetchResponse``() =
  let data = FetchResponseBinary
  let data = BinaryZipper(data)
  let (res:FetchResponse) = FetchResponse.Read (0s, data)
  let topicName, ps = res.topics.[0]
  let p, ec, _hwo, _, _, _, mss, ms = ps.[0]
  //let o, _ms, m = ms.messages.[0]
  let x = ms.messages.[0]
  let o, m = x.offset, x.message
  Assert.AreEqual("test", topicName)
  Assert.AreEqual(p, 0)
  Assert.AreEqual(ec, 0s)
  Assert.AreEqual(mss, 37)
  Assert.AreEqual(o, 7L)
  Assert.AreEqual(1940715388, int m.crc)
  Assert.AreEqual("hello world", (m.value |> Binary.toString))

//[<Test>]
//let ``FetchResponse.read should decode partial FetchResponse`` () =
//  let data = FetchResponseBinary
//  for trim in [1..35] do
//    let data = Binary.resize (data.Count - trim) data
//    let (res:FetchResponse), _ = FetchResponse.read data
//    let tn,ps = res.topics.[0]
//    let _p, _ec, _hwo, _mss, ms = ps.[0]
//    Assert.AreEqual(1, res.topics.Length)
//    Assert.AreEqual("test", tn)
//    Assert.AreEqual(0, ms.messages.Length)

//[<Test>]
//let ``FetchResponse.read should read sample FetchResponse`` () =
//  let file = @"C:\Users\eulerfx\Documents\GitHub\kafunk\tests\kafunk.Tests\sample_fetch_response.bin"
//  let bytes = System.IO.File.ReadAllBytes file |> Binary.ofArray
//  let res = FetchResponse.read bytes |> fst
//  ()
  
  
  


//[<Test>]
let ``ProduceResponse.read should decode ProduceResponse``() =
  let data =
    Binary.ofArray [|
      0uy;0uy;0uy;1uy;0uy;4uy;116uy;101uy;115uy;116uy;0uy;0uy;0uy;1uy;0uy;0uy;
      0uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;8uy; |]
  let bz = BinaryZipper(data)
  let (res:ProduceResponse) = ProduceResponse.Read (1s,bz)
  let topicName, ps = let x = res.topics.[0] in x.topic, x.partitions
  let p, ec, off, _ts = let x = ps.[0] in x.partition, x.errorCode, x.offset, x.timestamp
  Assert.AreEqual("test", topicName)
  Assert.AreEqual(0, p)
  Assert.AreEqual(0s, ec)
  Assert.AreEqual(8L, off)

//[<Test>]
//let ``ProduceRequest.write should encode ProduceRequest``() =
//  let req = 
//    ProduceRequest(RequiredAcks.Local)
//    ProduceRequest.ofMessageSetTopics 
//      [| "test", [| 0, (MessageSet.ofMessage (Message.ofBytes "hello world"B None)) |] |] RequiredAcks.Local 0
//  let data = toArraySeg ProduceRequest.size (fun x -> ProduceRequest.write (1s,x)) req |> Binary.toArray |> Array.toList
//  let expected = [
//    0uy;1uy;0uy;0uy;3uy;232uy;0uy;0uy;0uy;1uy;0uy;4uy;116uy;101uy;115uy;116uy;
//    0uy;0uy;0uy;1uy;0uy;0uy;0uy;0uy;0uy;0uy;0uy;37uy;0uy;0uy;0uy;0uy;0uy;0uy;
//    0uy;0uy;0uy;0uy;0uy;25uy;115uy;172uy;247uy;124uy;0uy;0uy;255uy;255uy;255uy;
//    255uy;0uy;0uy;0uy;11uy;104uy;101uy;108uy;108uy;111uy;32uy;119uy;111uy;
//    114uy;108uy;100uy; ]
//  Assert.True((expected = data))