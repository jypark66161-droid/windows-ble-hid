using Windows.Storage.Streams;
internal static class Buffers {
 public static IBuffer AsBuffer(this byte[] value) {
   using var w = new DataWriter();
   w.WriteBytes(value);
   return w.DetachBuffer();
 }
}
