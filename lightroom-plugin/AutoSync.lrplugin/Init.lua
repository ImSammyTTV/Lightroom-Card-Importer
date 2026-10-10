-- Adds photos that appear in the library folder to the catalog, in place (no copying, no moving).
-- Runs a background check shortly after Lightroom starts and then once a minute.
-- To turn it off: File > Plug-in Manager > Auto Sync Photos > Disable.

local LrApplication = import 'LrApplication'
local LrTasks = import 'LrTasks'
local LrFileUtils = import 'LrFileUtils'
local LrPathUtils = import 'LrPathUtils'
local LrDate = import 'LrDate'
local LrLogger = import 'LrLogger'
local LrPrefs = import 'LrPrefs'

-- The folder to watch. The Card Importer app writes its photo folder to
-- <app data>/CardImporter/library.txt, so changing the folder in the app is enough.
-- DEFAULT_LIBRARY is only used if that file doesn't exist.
local DEFAULT_LIBRARY = LrPathUtils.child(LrPathUtils.getStandardFilePath('pictures'), 'Photography/Photos')

local function currentLibrary()
  local hint = LrPathUtils.child(LrPathUtils.child(LrPathUtils.getStandardFilePath('appData'), 'CardImporter'), 'library.txt')
  if LrFileUtils.exists(hint) == 'file' then
    local text = LrFileUtils.readFile(hint)
    if text then
      text = text:gsub('^\239\187\191', '') -- UTF-8 byte order mark, if any
      text = text:gsub('^%s+', ''):gsub('%s+$', '')
      if text ~= '' then return text end
    end
  end
  return DEFAULT_LIBRARY
end
local INTERVAL = 60 -- seconds between checks
local EXT = {
  arw = true, dng = true, cr2 = true, cr3 = true, nef = true, raf = true, orf = true, rw2 = true, srf = true, sr2 = true, pef = true, -- RAW
  jpg = true, jpeg = true, heic = true, heif = true, hif = true,                                                                      -- photos
  mp4 = true, mov = true, mxf = true,                                                                                                 -- video
}

local log = LrLogger('AutoSyncPhotos')
log:enable('logfile')

local prefs = LrPrefs.prefsForPlugin()
local seen = {} -- paths already known to be in the catalog (or already attempted)

-- Files in the library folders for this year and last year that we have not seen yet.
local function collect()
  local found = {}
  local year = tonumber(LrDate.timeToUserFormat(LrDate.currentTime(), '%Y'))
  local library = currentLibrary()
  for y = year - 1, year do
    local dir = LrPathUtils.child(library, tostring(y))
    if LrFileUtils.exists(dir) == 'directory' then
      for path in LrFileUtils.recursiveFiles(dir) do
        local ext = LrPathUtils.extension(path):lower()
        if EXT[ext] and not seen[path] then
          found[#found + 1] = path
        end
      end
    end
    LrTasks.yield()
  end
  return found
end

local function scan()
  local catalog = LrApplication.activeCatalog()
  local toAdd = {}
  local found = collect()
  if #found > 0 then log:warn('Scan: ' .. #found .. ' unseen files to check') end
  for i, path in ipairs(found) do
    if catalog:findPhotoByPath(path) then
      seen[path] = true
    else
      toAdd[#toAdd + 1] = path
    end
    if i % 50 == 0 then LrTasks.yield() end
  end

  if #toAdd > 0 then
    local added = 0
    catalog:withWriteAccessDo('Auto sync new photos', function()
      for _, path in ipairs(toAdd) do
        local ok, err = LrTasks.pcall(function() catalog:addPhoto(path) end)
        seen[path] = true -- don't retry a failing file every minute
        if ok then
          added = added + 1
        else
          log:warn('Could not add ' .. path .. ': ' .. tostring(err))
        end
      end
    end, { timeout = 60 })
    log:warn('Added ' .. added .. ' of ' .. #toAdd .. ' new photos')
  end
end

log:warn('Auto Sync Photos 1.0.1 loaded')

LrTasks.startAsyncTask(function()
  LrTasks.sleep(15) -- let Lightroom finish starting
  while true do
    -- LrTasks.pcall, not pcall: scan() yields, which plain pcall forbids in Lightroom's Lua
    local ok, err = LrTasks.pcall(scan)
    if not ok then log:warn('Scan failed: ' .. tostring(err)) end

    local waited = 0
    while waited < INTERVAL and not prefs.scanNow do
      LrTasks.sleep(1)
      waited = waited + 1
    end
    prefs.scanNow = false
  end
end)
