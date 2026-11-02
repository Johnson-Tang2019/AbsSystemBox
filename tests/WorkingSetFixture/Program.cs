// SPDX-License-Identifier: GPL-3.0-or-later
if (args.Length != 1) return 2;
var bytes = GC.AllocateArray<byte>(96 * 1024 * 1024, pinned: true);
for (var i = 0; i < bytes.Length; i += 4096) bytes[i] = 123;
File.WriteAllText(args[0], Environment.ProcessId.ToString());
await Task.Delay(TimeSpan.FromMinutes(4));
GC.KeepAlive(bytes);
return 0;
