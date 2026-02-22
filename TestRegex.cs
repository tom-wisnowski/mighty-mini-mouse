using System;
using System.Text.RegularExpressions;

class Program {
    static void Main() {
        string path = @""\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02248a_PID&8266&Col01#a&d101ff200fb5&0&0000#{guid}"";
        
        var btMatch = Regex.Match(path, @""VID&([0-9A-Fa-f]+)_PID&([0-9A-Fa-f]+).*?([^a-zA-Z0-9])([0-9A-Fa-f]{12})([^a-zA-Z0-9])"", RegexOptions.IgnoreCase);
        if (btMatch.Success) {
            Console.WriteLine(""Matches MAC: "" + btMatch.Groups[4].Value);
        } else {
            Console.WriteLine(""No MAC match"");
        }
    }
}
