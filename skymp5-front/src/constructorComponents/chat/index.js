import React, { useState, useEffect, useLayoutEffect, useRef, useCallback } from 'react';
import Draggable from 'react-draggable';
import { ResizableBox } from 'react-resizable';
import ChatCheckbox from './checkbox';
import Dices from './dices';

import ChatCorner from '../../img/chat_corner.svg';
import Settings from './settings';
import SendButton from './sendButton';
import ChatInput from './input';
import { replaceIfMoreThan20 } from '../../utils/replaceIfMoreThan20';

import './styles.scss';
const MAX_LENGTH = 2000; // Max message length
const TIME_LIMIT = 1; // Seconds
const SHOUT_LIMIT = 180; // Seconds
const MAX_LINES = 10;
const MAX_SHOUT_LENGTH = 100;
const MAX_HISTORY_LENGTH = 20;
// How close to the bottom (in px) still counts as "following" new messages
const STICK_TO_BOTTOM_THRESHOLD = 24;

const SHOUTREGEXP = /№(.*?)№/gi;

// Chat is laid out in 1080p pixels and scaled to the actual screen height
const BASE_SCREEN_HEIGHT = 1080;
const MIN_UI_SCALE = 0.75;
const getUiScale = () => Math.max(MIN_UI_SCALE, window.innerHeight / BASE_SCREEN_HEIGHT);

const Chat = (props) => {
  const [uiScale, setUiScale] = useState(getUiScale);
  const [input, updateInput] = useState('');
  const [isInputFocus, changeInputFocus] = useState(false);
  const [hideNonRP, changeNonRPHide] = useState(false);
  const [disableDiceSounds, setDisableDiceSounds] = useState(false);
  const [disableDiceColors, setDisableDiceColors] = useState(false);
  const [isPouchOpened, setPouchOpened] = useState(0);
  const [moveChat, setMoveChat] = useState(false);
  const [showSendButton, setSendButtonShow] = useState(false);
  const [isSettingsOpened, setSettingsOpened] = useState(false);
  const [fontSize, setFontSize] = useState(16);
  const placeholder = props.placeholder;
  const isInputHidden = props.isInputHidden;
  const send = props.send;
  const [lastSendInputText, setLastSendInputText] = useState(0);

  const [doesIncludeShout, setIncludeShout] = useState(false);

  const [shoutLength, setShoutLength] = useState(0);

  const inputRef = useRef();

  const chatRef = useRef();

  const isReset = useRef(true);

  const shoutReset = useRef(true);

  const messagesHistory = useRef([]);

  const currentMessageInHistory = useRef(-1);

  const writtenMessage = useRef('');

  // Follow new messages unless the player has scrolled up to read history.
  // Only the player's own wheel scrolling stops following: scroll events also fire when
  // text reflows (font load, resolution change, filters), and those must not count.
  const scrollToBottomIfFollowing = () => {
    const list = chatRef.current;
    if (list && window.needToScroll) {
      list.scrollTop = list.scrollHeight;
    }
  };

  const handleScroll = () => {
    const list = chatRef.current;
    // Scroll positions can be fractional when the chat is scaled, so compare with a threshold
    if (list && list.scrollHeight - list.scrollTop - list.clientHeight <= STICK_TO_BOTTOM_THRESHOLD) {
      window.needToScroll = true;
    }
  };

  const handleWheel = (e) => {
    if (e.deltaY < 0) {
      window.needToScroll = false;
    }
  };

  useLayoutEffect(scrollToBottomIfFollowing, [props.messages, hideNonRP, fontSize, uiScale]);

  useEffect(() => {
    const list = chatRef.current;
    if (!list || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(scrollToBottomIfFollowing);
    observer.observe(list);
    document.fonts?.ready.then(scrollToBottomIfFollowing);
    return () => observer.disconnect();
  }, []);

  const setEndOfContenteditable = (elem) => {
    const sel = window.getSelection();
    sel.selectAllChildren(elem);
    sel.collapseToEnd();
  };

  const addMessageToHistory = (message) => {
    messagesHistory.current = [message, ...messagesHistory.current];
    if (messagesHistory.length > MAX_HISTORY_LENGTH) {
      messagesHistory.current = messagesHistory.current.slice(0, MAX_HISTORY_LENGTH);
    }
    currentMessageInHistory.current = -1;
    writtenMessage.current = '';
  };

  const sendMessage = useCallback((text) => {
    const shout = text.match(SHOUTREGEXP);
    const shoutLen = shout
      ? shout.reduce((acc, text) => {
        acc += text.length;
        return acc;
      }, 0)
      : 0;
    if (text !== '' && text.length <= MAX_LENGTH && isReset.current && shoutLen <= MAX_SHOUT_LENGTH && (shoutLen === 0 || shoutReset.current)) {
      if (send !== undefined) {
        const message = replaceIfMoreThan20(text.trim(), '\n', '', MAX_LINES);
        send(message);
        addMessageToHistory(message);
      }
      isReset.current = false;
      updateInput('');
      inputRef.current.innerHTML = '';
      inputRef.current.focus();
      if (shout) {
        shoutReset.current = false;
        setTimeout(() => {
          shoutReset.current = true;
        }, 1000 * SHOUT_LIMIT);
        setIncludeShout(false);
        setShoutLength(0);
      }
    }
  }, [send, updateInput, input, isReset.current, shoutReset.current, shoutLength, doesIncludeShout]);

  useEffect(() => {
    const onResize = () => setUiScale(getUiScale());
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  useEffect(() => {
    window.needToScroll = true;
    const interval = setInterval(() => {
      isReset.current = true;
    }, 1000 * TIME_LIMIT);
    return () => clearInterval(interval);
  }, []);

  useEffect(() => {
    const node = inputRef.current;
    const listener = (event) => {
      // Imitate message sending on Enter press
      if (event.code === 'Enter' && !event.shiftKey && inputRef.current) {
        event.preventDefault();
        sendMessage(input);
      }
      if (event.key === 'ArrowUp' && event.ctrlKey) {
        if (currentMessageInHistory.current === -1) {
          writtenMessage.current = input;
        }
        if (currentMessageInHistory.current + 1 < messagesHistory.current.length) {
          currentMessageInHistory.current = currentMessageInHistory.current + 1;
          updateInput(messagesHistory.current[currentMessageInHistory.current]);
          inputRef.current.innerHTML = messagesHistory.current[currentMessageInHistory.current];
          setEndOfContenteditable(inputRef.current);
        }
      }
      if (event.key === 'ArrowDown' && event.ctrlKey) {
        if (currentMessageInHistory.current >= 0) {
          if (currentMessageInHistory.current === 0) {
            updateInput(writtenMessage.current);
            inputRef.current.innerHTML = writtenMessage.current;
            setEndOfContenteditable(inputRef.current);
            currentMessageInHistory.current = -1;
          } else {
            currentMessageInHistory.current = currentMessageInHistory.current - 1;
            updateInput(messagesHistory.current[currentMessageInHistory.current]);
            inputRef.current.innerHTML = messagesHistory.current[currentMessageInHistory.current];
            setEndOfContenteditable(inputRef.current);
          }
        }
      }
    };
    node?.addEventListener('keydown', listener);
    return () => node?.removeEventListener('keydown', listener);
  }, [inputRef.current, input]);

  useEffect(() => {
    if (inputRef !== undefined && inputRef.current !== undefined && !isInputHidden) {
      inputRef.current.focus();
    }
  }, [isInputHidden]);

  useEffect(() => {
    if (inputRef !== undefined && inputRef.current !== undefined) {
      inputRef.current.focus();
    }
  }, [props.messages]);

  const handleInput = (value) => {
    updateInput(value);
    const shout = value.match(SHOUTREGEXP);
    if (shout && shout[0] !== '') {
      setIncludeShout(true);
      setShoutLength(shout.reduce((acc, text) => {
        acc += text.length;
        return acc;
      }, 0));
    } else {
      setIncludeShout(false);
      setShoutLength(0);
    }
  };

  const getMessageSpans = (message) => {
    let isNonRp = message.category === 'plain';
    const result = message.text.map(({ text, color, opacity, type }, i) => {
      if (i >= 1) {
        isNonRp = (type.includes('nonrp') && isNonRp);
      }
      return <span key={`${text}_${i}`} style={{ color: `${color}`, opacity: opacity }} className={`${type.join(' ')}`}>{text}</span>;
    });
    return [result, isNonRp];
  };

  const getList = () => {
    return window.chatMessages.map((msg, index) => {
      const result = getMessageSpans(msg);
      return (
        <div
          className={`msg ${result[1] ? 'nonrp' : ''}`}
          key={`msg-${index}`}
          style={{ opacity: msg.opacity }}
        >
          {
            result[0]
          }
        </div>
      );
    });
  };
  return (
    <div className='fullPage'>
      <Draggable handle='#handle' disabled={!moveChat} bounds={'.fullPage'}>
        <div id='chat'>
          <div className='chat-scale' style={{ transform: `scale(${uiScale})` }}>
          <div className="chat-main">
            <ResizableBox
              height={320}
              maxConstraints={[800, 1100]}
              minConstraints={[320, 320]}
              axis={'y'}
              transformScale={uiScale}
              handle={
                 !isInputHidden &&
                 <div className='chat-corner'>
                   <img src={ChatCorner} />
                 </div>
              }
              resizeHandles={['se']}
              className={`list ${hideNonRP ? 'hideNonRP' : ''}`}
              id='handle'
            >
              <div className='chat-list' style={{ fontSize }} ref={chatRef} onScroll={handleScroll} onWheel={handleWheel}>
                <div className='chat-messages'>
                  {getList()}
                </div>
              </div>
            </ResizableBox>
            <div
              style={{
                height: '100px',
                display: !isInputHidden ? 'none' : 'block'
              }}
            />
            <div
              className='input'
              style={{
                display: isInputHidden ? 'none' : 'block'
              }}
            >
              <div className='chat-input'>
                <ChatInput
                  id="chatInput"
                  className={'show'}
                  type="text"
                  placeholder={placeholder !== undefined ? placeholder : ''}
                  onChange={(value) => {
                    handleInput(value);
                    if (lastSendInputText + 1000 < Date.now()) {
                      window.skyrimPlatform.sendMessage('onInput');
                      setLastSendInputText(Date.now());
                    }
                  }}
                  onFocus={(e) => changeInputFocus(true)}
                  onBlur={(e) => changeInputFocus(false)}
                  ref={inputRef}
                  fontSize={fontSize}
                  maxLines={MAX_LINES}
                />
                {
                  showSendButton && <SendButton onClick={() => sendMessage(input)} />
                }
              </div>
              <div className='chat-checkboxes'>
                <ChatCheckbox
                  id={'nonrp'}
                  text={'non-rp'}
                  isChecked={hideNonRP}
                  onChange={(e) => {
                    inputRef.current.focus();
                    changeNonRPHide(e.target.checked);
                  }} />
                <ChatCheckbox
                  id={'settings'}
                  text={'settings'}
                  isChecked={isSettingsOpened}
                  onChange={(e) => {
                    inputRef.current.focus();
                    setSettingsOpened(e.target.checked);
                  }} />
                {/* Maybe we will need it later: <ChatCheckbox id={'diceColor'} text={'dice colors'} isChecked={!disableDiceColors} onChange={(e) => setDisableDiceColors(!e.target.checked)} /> */}
                <ChatCheckbox
                  id={'moveChat'}
                  text={'move chat'}
                  isChecked={moveChat}
                  onChange={(e) => {
                    inputRef.current.focus();
                    setMoveChat(e.target.checked);
                  }} />
                { doesIncludeShout &&
                  <span className={`chat-message-limit shout-limit ${shoutLength > MAX_SHOUT_LENGTH ? 'limit' : ''} text`}>{shoutLength}/{MAX_SHOUT_LENGTH}</span>
                }
                <span className={`chat-message-limit ${input.length > MAX_LENGTH ? 'limit' : ''} text`}>{input.length}/{MAX_LENGTH}</span>
              </div>
            </div>
          </div>
          {
            isInputHidden
              ? <></>
              : <Dices
                  isOpened={isPouchOpened}
                  setOpened={setPouchOpened}
                  send={props.send}
                  disableSound={disableDiceSounds}
                  inputRef={inputRef}
              />
          }
          </div>
        </div>
      </Draggable>
      {
        (isSettingsOpened && !isInputHidden) &&
        <div className='chat-settings-scale' style={{ transform: `scale(${uiScale})` }}>
          <Settings
            fontSize={fontSize}
            setFontSize={setFontSize}
            isSoundsDisabled={disableDiceSounds}
            setDisableSounds={setDisableDiceSounds}
            showSendButton={showSendButton}
            setShowSendButton={setSendButtonShow}
          />
        </div>
      }
    </div>
  );
};

export default Chat;
