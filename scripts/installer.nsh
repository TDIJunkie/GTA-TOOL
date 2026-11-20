; Zusätzliche Verknüpfungen: Vehicle Studio direkt starten
!macro customInstall
  CreateShortCut "$DESKTOP\GTA Tool - Vehicle Studio.lnk" "$INSTDIR\GTA Tool.exe" "--vehicle-studio" "$INSTDIR\GTA Tool.exe" 0
  CreateShortCut "$SMPROGRAMS\GTA Tool - Vehicle Studio.lnk" "$INSTDIR\GTA Tool.exe" "--vehicle-studio" "$INSTDIR\GTA Tool.exe" 0
  CreateShortCut "$DESKTOP\GTA Tool - Clothing Studio.lnk" "$INSTDIR\GTA Tool.exe" "--clothing-studio" "$INSTDIR\GTA Tool.exe" 0
  CreateShortCut "$SMPROGRAMS\GTA Tool - Clothing Studio.lnk" "$INSTDIR\GTA Tool.exe" "--clothing-studio" "$INSTDIR\GTA Tool.exe" 0
  CreateShortCut "$DESKTOP\GTA Tool - MLO Studio.lnk" "$INSTDIR\GTA Tool.exe" "--mlo-studio" "$INSTDIR\GTA Tool.exe" 0
  CreateShortCut "$SMPROGRAMS\GTA Tool - MLO Studio.lnk" "$INSTDIR\GTA Tool.exe" "--mlo-studio" "$INSTDIR\GTA Tool.exe" 0
  CreateShortCut "$DESKTOP\GTA Tool - Animation Studio.lnk" "$INSTDIR\GTA Tool.exe" "--anim-studio" "$INSTDIR\GTA Tool.exe" 0
  CreateShortCut "$SMPROGRAMS\GTA Tool - Animation Studio.lnk" "$INSTDIR\GTA Tool.exe" "--anim-studio" "$INSTDIR\GTA Tool.exe" 0
!macroend

!macro customUnInstall
  Delete "$DESKTOP\GTA Tool - MLO Studio.lnk"
  Delete "$SMPROGRAMS\GTA Tool - MLO Studio.lnk"
  Delete "$DESKTOP\GTA Tool - Animation Studio.lnk"
  Delete "$SMPROGRAMS\GTA Tool - Animation Studio.lnk"
  Delete "$DESKTOP\GTA Tool - Vehicle Studio.lnk"
  Delete "$SMPROGRAMS\GTA Tool - Vehicle Studio.lnk"
  Delete "$DESKTOP\GTA Tool - Clothing Studio.lnk"
  Delete "$SMPROGRAMS\GTA Tool - Clothing Studio.lnk"
!macroend
