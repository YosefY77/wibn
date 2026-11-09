' Harmoni - فتح التطبيق بدون شاشة سوداء
Set sh = CreateObject("WScript.Shell")
sh.CurrentDirectory = "C:\Users\YSIF\Documents\music_app"
' pythonw = بدون كونسول، والسيرفر نفسه يفتح نافذة التطبيق
sh.Run """C:\Users\YSIF\AppData\Local\Programs\Python\Python312\pythonw.exe"" -X utf8 server.py", 0, False
