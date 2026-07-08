@echo off
rem VRChat Personal Space GUI ランチャー（ダブルクリックで起動・コンソール無し）
rem うまく起動しない場合は、この .bat のあるフォルダで
rem     python personal_space_gui.py
rem を実行するとエラー内容が確認できます。
cd /d "%~dp0"
start "" pythonw personal_space_gui.py
