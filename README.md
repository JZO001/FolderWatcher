# FolderWatcher
Watch file changes in a directory and notify development web server

For details, please visit: https://www.jzo.hu/blog/1002


You can configure this console application in the configuration file or you can add parameters at startup:

/DefaultFolderToWatch="": the directory which will be observed. Files and subfolders are also included.

/ExcludeSubFolders="": exclude files and directories. Use ; to separate items.

/CommandAtChange="": what command need to be executed, if changes detected

/CommandWorkFolder="": the work folder of the command execution. By default, this is the same as "DefaultFolderToWatch".

/CommandDelayMS=1000: execution delay in miliseconds.

/UseShellExecute=false: use shell execute, if it is neccessary

/RefreshNotificationURL="": if the value is set, client will send notification to the given URL

